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
[Trait("Prerequisite", "Postgres")]
public class NormalizedDescriptionNearestNeighbourTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task MergeAsync_DeletingANeighbour_NullsTheReferenceInsteadOfDeletingTheReferrer()
	{
		// The FK is ON DELETE SET NULL rather than Cascade. If that ever regresses to Cascade,
		// merging one canonical row away would silently delete every unrelated pending row that
		// merely cited it as its nearest neighbour — data loss disguised as a merge.

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

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	// Fixed vectors keep merge rescoring deterministic without external model access.
	private sealed class NoOpEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> Task.FromResult(new float[OnnxEmbeddingService.EmbeddingDimension]);

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> Task.FromResult(texts.Select(_ => new float[OnnxEmbeddingService.EmbeddingDimension]).ToList());
	}

}
