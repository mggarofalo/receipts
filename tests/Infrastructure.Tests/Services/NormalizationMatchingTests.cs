using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pgvector;

namespace Infrastructure.Tests.Services;

public class NormalizationMatchingTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	private readonly Mock<IEmbeddingService> _embeddingServiceMock = new();
	private readonly NormalizedDescriptionMapper _mapper = new();
	private readonly NormalizedDescriptionSettingsMapper _settingsMapper = new();

	public NormalizationMatchingTests()
	{
		(_contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user";
	}

	[Theory]
	[InlineData(0.9, "AutoAccept", NormalizedDescriptionStatus.Active, true)]
	[InlineData(0.899999, "PendingReview", NormalizedDescriptionStatus.PendingReview, false)]
	[InlineData(0.5, "PendingReview", NormalizedDescriptionStatus.PendingReview, false)]
	[InlineData(0.499999, "CreateNew", NormalizedDescriptionStatus.Active, false)]
	public async Task Matching_ExecutionAndSimulation_AgreeAtThresholdBoundaries(
		double similarity, string expectedOutcome, NormalizedDescriptionStatus expectedStatus, bool reusesCandidate)
	{
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "Whole milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			seed.NormalizedDescriptionSettings.Add(new NormalizedDescriptionSettingsEntity
			{
				Id = Infrastructure.Configurations.NormalizedDescriptionSettingsEntityConfiguration.SingletonId,
				AutoAcceptThreshold = 0.9,
				PendingReviewThreshold = 0.5,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		_embeddingServiceMock.SetupGet(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock.Setup(e => e.GenerateEmbeddingAsync("MLK", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new float[] { 1, 0 });
		TestableNormalizedDescriptionService service = new(
			_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper, id, similarity);

		MatchTestResult preview = await service.TestMatchAsync("MLK", 5, null, null, CancellationToken.None);
		GetOrCreateResult actual = await service.GetOrCreateAsync("MLK", CancellationToken.None);

		preview.SimulatedOutcome.Should().Be(expectedOutcome);
		preview.SimulatedTargetId.Should().Be(reusesCandidate ? id : null);
		actual.Description.Status.Should().Be(expectedStatus);
		(actual.Description.Id == id).Should().Be(reusesCandidate);
		actual.MatchScore.Should().Be(expectedOutcome == "CreateNew" ? null : similarity);
	}

	[Fact]
	public async Task TestMatchAsync_ExactRejectedMatch_ReportsSuppressionWithoutTargetOrWrites()
	{
		Guid id = Guid.NewGuid();
		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = id,
				CanonicalName = "MISC",
				Status = NormalizedDescriptionStatus.Rejected,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		NormalizedDescriptionService service = new(_contextFactory, _embeddingServiceMock.Object, _mapper, _settingsMapper);

		MatchTestResult preview = await service.TestMatchAsync(" misc ", 5, null, null, CancellationToken.None);
		GetOrCreateResult actual = await service.GetOrCreateAsync(" misc ", CancellationToken.None);

		preview.SimulatedOutcome.Should().Be("Rejected");
		preview.SimulatedTargetId.Should().BeNull();
		preview.Candidates.Should().ContainSingle().Which.Status.Should().Be("Rejected");
		actual.IsRejected.Should().BeTrue();
		actual.MatchScore.Should().BeNull();
		using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		(await verify.NormalizedDescriptions.CountAsync()).Should().Be(1);
		(await verify.NormalizedDescriptions.SingleAsync()).Status.Should().Be(NormalizedDescriptionStatus.Rejected);
		_embeddingServiceMock.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	private sealed class TestableNormalizedDescriptionService(
		IDbContextFactory<ApplicationDbContext> contextFactory,
		IEmbeddingService embeddingService,
		NormalizedDescriptionMapper mapper,
		NormalizedDescriptionSettingsMapper settingsMapper,
		Guid matchId,
		double similarity) : NormalizedDescriptionService(contextFactory, embeddingService, mapper, settingsMapper)
	{
		private readonly Guid _matchId = matchId;
		private readonly double _similarity = similarity;

		protected override async Task<(NormalizedDescriptionEntity? Match, double? Similarity)> AnnSearchTopOneAsync(
			ApplicationDbContext context, Vector queryVector, CancellationToken cancellationToken)
		{
			NormalizedDescriptionEntity? match = await context.NormalizedDescriptions
				.FirstOrDefaultAsync(e => e.Id == _matchId, cancellationToken);
			return (match, _similarity);
		}

		protected override async Task<List<MatchCandidate>> AnnSearchTopNAsync(
			ApplicationDbContext context, Vector queryVector, int topN, CancellationToken cancellationToken)
		{
			NormalizedDescriptionEntity? match = await context.NormalizedDescriptions
				.FirstOrDefaultAsync(e => e.Id == _matchId, cancellationToken);
			if (match is null)
			{
				return [];
			}

			return [new MatchCandidate(match.Id, match.CanonicalName, _similarity, match.Status.ToString())];
		}
	}
}
