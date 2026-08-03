using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

// Real-Postgres coverage for the self-referencing nearest-neighbour FK added in RECEIPTS-873.
// These behaviours are invisible to the InMemory unit tests because that provider enforces no
// foreign keys at all: the SetNull cleanup never fires and the constraint never rejects a
// dangling reference, so both paths below would silently "pass" for the wrong reason.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NormalizedDescriptionNearestNeighbourTests(PostgresFixture fixture)
{
	[Fact]
	public async Task MergeAsync_DeletingANeighbour_NullsTheReferenceInsteadOfDeletingTheReferrer()
	{
		// The FK is ON DELETE SET NULL rather than Cascade. If that ever regresses to Cascade,
		// merging one canonical row away would silently delete every unrelated pending row that
		// merely cited it as its nearest neighbour — data loss disguised as a merge.
		await ResetTablesAsync();

		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		Guid pendingId = Guid.NewGuid();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.AddRange(
				Row(keepId, "Strawberry Jam"),
				Row(discardId, "Strawberry Conserve"),
				// Cites the row that is about to be merged away.
				Row(pendingId, "Strawberry Preserves", NormalizedDescriptionStatus.PendingReview, discardId, 0.86));
			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = BuildService();

		await service.MergeAsync(keepId, discardId, CancellationToken.None);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		NormalizedDescriptionEntity? survivor = await verify.NormalizedDescriptions
			.AsNoTracking()
			.SingleOrDefaultAsync(e => e.Id == pendingId);

		survivor.Should().NotBeNull("merging away a neighbour must not delete the rows that referenced it");
		survivor!.NearestNeighbourId.Should().BeNull();

		// The score is deliberately left behind: it is still a true record of what was observed,
		// and the UI renders it as "nearly matched a since-removed entry".
		survivor.NearestNeighbourSimilarity.Should().Be(0.86);
	}

	[Fact]
	public async Task GetOrCreateAsync_NeighbourDeletedMidFlight_StillCreatesTheRowWithoutTheNearMiss()
	{
		// MergeAsync can delete the ANN top-1 match in the window between the search and the
		// insert, which makes the new row's NearestNeighbourId dangle and Postgres reject the
		// INSERT with an FK violation. Resolution must survive that: the near-miss is evidence,
		// not essential data, so the row is written without it rather than failing outright.
		await ResetTablesAsync();

		Guid phantomId = Guid.NewGuid();

		PhantomNeighbourService service = new(
			new FixtureDbContextFactory(fixture),
			new NoOpEmbeddingService(),
			new NormalizedDescriptionMapper(),
			new NormalizedDescriptionSettingsMapper(),
			phantomId,
			// Between the two thresholds, so GetOrCreateAsync takes the PendingReview branch and
			// tries to persist the (now-deleted) neighbour.
			(NormalizedDescriptionService.InitialAutoAcceptThreshold + NormalizedDescriptionService.InitialPendingReviewThreshold) / 2);

		GetOrCreateResult result = await service.GetOrCreateAsync("Vanishing Neighbour", CancellationToken.None);

		result.Description.CanonicalName.Should().Be("Vanishing Neighbour");
		result.Description.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
		result.Description.NearestNeighbourId.Should().BeNull("the cited row no longer exists");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		NormalizedDescriptionEntity stored = await verify.NormalizedDescriptions
			.AsNoTracking()
			.SingleAsync(e => e.Id == result.Description.Id);
		stored.NearestNeighbourId.Should().BeNull();
	}

	private NormalizedDescriptionService BuildService() => new(
		new FixtureDbContextFactory(fixture),
		new NoOpEmbeddingService(),
		new NormalizedDescriptionMapper(),
		new NormalizedDescriptionSettingsMapper());

	private static NormalizedDescriptionEntity Row(
		Guid id,
		string canonicalName,
		NormalizedDescriptionStatus status = NormalizedDescriptionStatus.Active,
		Guid? nearestNeighbourId = null,
		double? nearestNeighbourSimilarity = null) => new()
		{
			Id = id,
			CanonicalName = canonicalName,
			Status = status,
			CreatedAt = DateTimeOffset.UtcNow,
			NearestNeighbourId = nearestNeighbourId,
			NearestNeighbourSimilarity = nearestNeighbourSimilarity,
		};

	// Returns a top-1 match that is not in the database, standing in for a neighbour that
	// MergeAsync deleted after the ANN search read it.
	private sealed class PhantomNeighbourService(
		IDbContextFactory<ApplicationDbContext> contextFactory,
		IEmbeddingService embeddingService,
		NormalizedDescriptionMapper mapper,
		NormalizedDescriptionSettingsMapper settingsMapper,
		Guid phantomId,
		double similarity) : NormalizedDescriptionService(contextFactory, embeddingService, mapper, settingsMapper)
	{
		protected override Task<(NormalizedDescriptionEntity? Match, double? Similarity)> AnnSearchTopOneAsync(
			ApplicationDbContext context, Vector queryVector, CancellationToken cancellationToken)
		{
			NormalizedDescriptionEntity phantom = new()
			{
				Id = phantomId,
				CanonicalName = "Already Merged Away",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			};
			return Task.FromResult<(NormalizedDescriptionEntity?, double?)>((phantom, similarity));
		}
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	// The integration project doesn't reference Moq. IsConfigured=true keeps GetOrCreateAsync on
	// the embedding path; a non-empty vector is required for the ANN branch to be reached at all.
	private sealed class NoOpEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> Task.FromResult(new float[OnnxEmbeddingService.EmbeddingDimension]);

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> Task.FromResult(texts.Select(_ => new float[OnnxEmbeddingService.EmbeddingDimension]).ToList());
	}

	private async Task ResetTablesAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}
}
