using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for the pending-description requeue (RECEIPTS-883).
//
// The InMemory suite cannot prove the parts that matter here. It enforces no FKs, so it cannot
// show that deleting a pending row which another row cites as its nearest neighbour is legal
// (the self-FK added in RECEIPTS-873 is ON DELETE SET NULL, and a RESTRICT would raise 23503).
// It also does not replay cascades for store-resident rows the change tracker never saw, so a
// stale NormalizedDescriptionMatchScore paired with a nulled FK survives there unnoticed —
// which is precisely the inconsistent state the checklist asks us to rule out.
//
// The fixture is one Postgres instance shared by the whole collection with no per-test rollback,
// and sibling tests seed their own PendingReview rows. Since the requeue is deliberately global
// ("every pending row"), each test here clears the pending set first so the counts under test are
// exactly the ones it seeded, and every assertion is scoped to its own rows.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NormalizedDescriptionRequeueTests(PostgresFixture fixture)
{
	[Fact]
	public async Task RequeuePendingAsync_LeavesNoItemWithANullLinkAndALingeringScore()
	{
		// Arrange — a pending row citing a second pending row as its nearest neighbour, with a
		// live item and a trashed item hanging off it, plus an untouched Active row.
		await ClearPendingAsync();

		string token = Guid.NewGuid().ToString("N")[..8];
		Guid pendingId = Guid.NewGuid();
		Guid neighbourId = Guid.NewGuid();
		Guid activeId = Guid.NewGuid();
		Guid liveItemId = Guid.NewGuid();
		Guid trashedItemId = Guid.NewGuid();
		Guid activeItemId = Guid.NewGuid();

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Receipts.Add(receipt);
			setup.NormalizedDescriptions.AddRange(
				BuildNormalized(neighbourId, $"Strawberry Jam {token}", DomainStatus.PendingReview),
				BuildNormalized(activeId, $"Organic Milk {token}", DomainStatus.Active));
			await setup.SaveChangesAsync();

			// Added after its neighbour so the self-FK resolves.
			NormalizedDescriptionEntity pending = BuildNormalized(pendingId, $"Strawbery Jam {token}", DomainStatus.PendingReview);
			pending.NearestNeighbourId = neighbourId;
			pending.NearestNeighbourSimilarity = 0.86;
			setup.NormalizedDescriptions.Add(pending);
			await setup.SaveChangesAsync();

			ReceiptItemEntity liveItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			liveItem.Id = liveItemId;
			liveItem.NormalizedDescriptionId = pendingId;
			liveItem.NormalizedDescriptionMatchScore = 0.86;

			ReceiptItemEntity trashedItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			trashedItem.Id = trashedItemId;
			trashedItem.NormalizedDescriptionId = pendingId;
			trashedItem.NormalizedDescriptionMatchScore = 0.86;
			trashedItem.DeletedAt = DateTimeOffset.UtcNow;

			ReceiptItemEntity activeItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			activeItem.Id = activeItemId;
			activeItem.NormalizedDescriptionId = activeId;
			activeItem.NormalizedDescriptionMatchScore = 0.99;

			setup.ReceiptItems.AddRange(liveItem, trashedItem, activeItem);
			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = CreateService();

		RequeuePendingPreview preview = await service.PreviewRequeuePendingAsync(CancellationToken.None);
		preview.PendingDescriptionCount.Should().Be(2);
		preview.LinkedItemCount.Should().Be(1, "the preview counts live items only");
		preview.StaleMatchScoreCount.Should().Be(1);

		// Act — deleting `pending` and `neighbour` together exercises the self-FK: one of the two
		// is removed while the other still cites it. A RESTRICT here would raise 23503.
		RequeuePendingResult? result = await service.RequeuePendingAsync(preview.PendingDescriptionCount, CancellationToken.None);

		// Assert
		result.Should().NotBeNull();
		result!.DeletedDescriptionCount.Should().Be(2);
		result.UnlinkedItemCount.Should().Be(1);
		result.ClearedMatchScoreCount.Should().Be(1);

		await using ApplicationDbContext verify = fixture.CreateDbContext();

		List<Guid> survivingIds = await verify.NormalizedDescriptions
			.AsNoTracking()
			.Where(e => e.Id == pendingId || e.Id == neighbourId || e.Id == activeId)
			.Select(e => e.Id)
			.ToListAsync();
		survivingIds.Should().BeEquivalentTo([activeId], "only the Active row may survive");

		// The invariant the issue asks to verify afterwards, checked against real rows rather than
		// the change tracker. Scoped to this test's receipt so a sibling test's data cannot mask
		// or manufacture a violation.
		List<ReceiptItemEntity> orphanedScores = await verify.ReceiptItems
			.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(r => r.ReceiptId == receipt.Id)
			.Where(r => r.NormalizedDescriptionId == null && r.NormalizedDescriptionMatchScore != null)
			.ToListAsync();
		orphanedScores.Should().BeEmpty(
			"a null FK paired with a non-null match score is exactly the inconsistent window this requeue exists to close");

		ReceiptItemEntity trashedAfter = await verify.ReceiptItems
			.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.SingleAsync(r => r.Id == trashedItemId);
		trashedAfter.NormalizedDescriptionId.Should().BeNull();
		trashedAfter.NormalizedDescriptionMatchScore.Should().BeNull();
		trashedAfter.DeletedAt.Should().NotBeNull("the requeue must not resurrect trashed items");

		ReceiptItemEntity activeAfter = await verify.ReceiptItems
			.IgnoreAutoIncludes().AsNoTracking()
			.SingleAsync(r => r.Id == activeItemId);
		activeAfter.NormalizedDescriptionId.Should().Be(activeId, "Active entries are never touched");
		activeAfter.NormalizedDescriptionMatchScore.Should().Be(0.99);

		// The live item is now exactly what the background resolver's candidate query looks for.
		ReceiptItemEntity liveAfter = await verify.ReceiptItems
			.IgnoreAutoIncludes().AsNoTracking()
			.SingleAsync(r => r.Id == liveItemId);
		liveAfter.NormalizedDescriptionId.Should().BeNull();
		liveAfter.DeletedAt.Should().BeNull();

		// And a re-run is a clean no-op rather than an error.
		RequeuePendingResult? rerun = await service.RequeuePendingAsync(0, CancellationToken.None);
		rerun.Should().NotBeNull();
		rerun!.DeletedDescriptionCount.Should().Be(0);

		RequeuePendingPreview after = await service.PreviewRequeuePendingAsync(CancellationToken.None);
		after.PendingDescriptionCount.Should().Be(0);
		after.LinkedItemCount.Should().Be(0);
		after.StaleMatchScoreCount.Should().Be(0);
	}

	[Fact]
	public async Task RequeuePendingAsync_CountMismatch_CommitsNothing()
	{
		await ClearPendingAsync();

		string token = Guid.NewGuid().ToString("N")[..8];
		Guid pendingId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Receipts.Add(receipt);
			setup.NormalizedDescriptions.Add(BuildNormalized(pendingId, $"Strawbery Jam {token}", DomainStatus.PendingReview));
			await setup.SaveChangesAsync();

			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item.Id = itemId;
			item.NormalizedDescriptionId = pendingId;
			item.NormalizedDescriptionMatchScore = 0.86;
			setup.ReceiptItems.Add(item);
			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = CreateService();

		// The caller previewed a world with 7 pending rows; there is 1. Nothing may be destroyed.
		RequeuePendingResult? result = await service.RequeuePendingAsync(7, CancellationToken.None);

		result.Should().BeNull();

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		bool pendingSurvives = await verify.NormalizedDescriptions
			.AsNoTracking().AnyAsync(e => e.Id == pendingId);
		pendingSurvives.Should().BeTrue("a rejected guard must commit nothing");

		ReceiptItemEntity untouched = await verify.ReceiptItems
			.IgnoreAutoIncludes().AsNoTracking().SingleAsync(r => r.Id == itemId);
		untouched.NormalizedDescriptionId.Should().Be(pendingId);
		untouched.NormalizedDescriptionMatchScore.Should().Be(0.86);
	}

	// Sibling tests in this collection leave PendingReview rows behind, and the requeue is global
	// by design. Clearing first makes "how many pending rows exist" a fact this test controls.
	private async Task ClearPendingAsync()
	{
		NormalizedDescriptionService service = CreateService();
		RequeuePendingPreview preview = await service.PreviewRequeuePendingAsync(CancellationToken.None);
		await service.RequeuePendingAsync(preview.PendingDescriptionCount, CancellationToken.None);
	}

	private NormalizedDescriptionService CreateService() => new(
		new FixtureContextFactory(fixture),
		new UnconfiguredEmbeddingService(),
		new NormalizedDescriptionMapper(),
		new NormalizedDescriptionSettingsMapper());

	private static NormalizedDescriptionEntity BuildNormalized(Guid id, string canonicalName, DomainStatus status) => new()
	{
		Id = id,
		CanonicalName = canonicalName,
		Status = status,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	// The requeue never embeds anything, so an unconfigured stub keeps the test off the ONNX model.
	private sealed class UnconfiguredEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => false;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			Task.FromResult(Array.Empty<float>());

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) =>
			Task.FromResult(new List<float[]>());
	}
}
