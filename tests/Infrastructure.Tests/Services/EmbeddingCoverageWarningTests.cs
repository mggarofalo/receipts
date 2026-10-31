using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Pgvector;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class EmbeddingCoverageWarningTests
{
	private readonly Mock<ILogger> _logger = new();
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

	public EmbeddingCoverageWarningTests()
	{
		(_contextFactory, _) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
	}

	[Theory]
	[InlineData(0, 20)]
	[InlineData(18, 20)]
	public async Task CheckAsync_BelowNinetyFivePercent_WarnsOncePerFingerprint(int current, int total)
	{
		await SeedAsync(current, total);
		EmbeddingCoverageWarning warning = new(_logger.Object);

		await using ApplicationDbContext context = _contextFactory.CreateDbContext();
		await warning.CheckAsync(context, CancellationToken.None);
		await warning.CheckAsync(context, CancellationToken.None);

		VerifyWarnings(Times.Once());
	}

	[Theory]
	[InlineData(19, 20)]
	[InlineData(20, 20)]
	[InlineData(0, 0)]
	public async Task CheckAsync_AtOrAboveNinetyFivePercentOrEmpty_StaysQuiet(int current, int total)
	{
		await SeedAsync(current, total);
		EmbeddingCoverageWarning warning = new(_logger.Object);

		await using ApplicationDbContext context = _contextFactory.CreateDbContext();
		await warning.CheckAsync(context, CancellationToken.None);

		VerifyWarnings(Times.Never());
	}

	[Fact]
	public async Task CheckAsync_CoverageThatFallsAfterAQuietCheck_StillWarns()
	{
		await SeedAsync(20, 20);
		EmbeddingCoverageWarning warning = new(_logger.Object);
		await using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await warning.CheckAsync(context, CancellationToken.None);
		}

		await SeedAsync(0, 5);
		await using (ApplicationDbContext context = _contextFactory.CreateDbContext())
		{
			await warning.CheckAsync(context, CancellationToken.None);
		}

		VerifyWarnings(Times.Once());
	}

	[Fact]
	public async Task CheckAsync_WhenTheCountFails_StaysQuietAndDoesNotThrow()
	{
		EmbeddingCoverageWarning warning = new(_logger.Object);
		ApplicationDbContext context = _contextFactory.CreateDbContext();
		await context.DisposeAsync();

		Func<Task> act = () => warning.CheckAsync(context, CancellationToken.None);

		await act.Should().NotThrowAsync();
		VerifyWarnings(Times.Never());
	}

	private void VerifyWarnings(Times times) => _logger.Verify(
		logger => logger.Log(
			LogLevel.Warning,
			It.IsAny<EventId>(),
			It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(OnnxEmbeddingService.EmbeddingSpaceFingerprint)),
			It.IsAny<Exception?>(),
			It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
		times);

	/// <summary>Adds <paramref name="total"/> searchable names, the first <paramref name="current"/> at the current fingerprint.</summary>
	private async Task SeedAsync(int current, int total)
	{
		await using ApplicationDbContext seed = _contextFactory.CreateDbContext();
		for (int index = 0; index < total; index++)
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = Guid.NewGuid(),
				CanonicalName = $"Catalog name {Guid.NewGuid():N}",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
				Embedding = new Vector(new float[OnnxEmbeddingService.EmbeddingDimension]),
				EmbeddingModelVersion = index < current ? OnnxEmbeddingService.EmbeddingSpaceFingerprint : "previous-fingerprint",
			});
		}
		await seed.SaveChangesAsync();
	}
}
