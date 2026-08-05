using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using SampleData.Entities;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for NormalizedDescriptionService.MergeAsync.
//
// Merge deletes the discarded canonical row, and ReceiptItem.NormalizedDescriptionId is
// DeleteBehavior.SetNull — so every row still pointing at the discard when it is removed
// silently loses its canonical link. The InMemory unit suite cannot prove any of this: it
// enforces no FKs and does not replay cascades for store-resident rows the change tracker
// never saw, so a dangling id survives there that Postgres would have nulled.
//
// The soft-delete case is the one that actually bites: ReceiptItems carry a
// `DeletedAt == null` query filter, so a trashed item is invisible to the re-link query,
// gets SetNull'd, and comes back from the recycle bin unlinked — with no error anywhere.
// This mirrors the fix already made for transactions in AccountMergeService (RECEIPTS-801).
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NormalizedDescriptionMergeIntegrityTests(PostgresFixture fixture)
{
	[Fact]
	public async Task MergeAsync_RepointsSoftDeletedReceiptItems_InsteadOfNullingTheirLink()
	{
		// Arrange — one live and one soft-deleted ReceiptItem, both pointing at the discard row.
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		Guid liveItemId = Guid.NewGuid();
		Guid trashedItemId = Guid.NewGuid();

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Receipts.Add(receipt);
			setup.NormalizedDescriptions.AddRange(
				BuildNormalized(keepId, "Organic Milk"),
				BuildNormalized(discardId, "Orgnaic Mlik"));
			await setup.SaveChangesAsync();

			ReceiptItemEntity liveItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			liveItem.Id = liveItemId;
			liveItem.NormalizedDescriptionId = discardId;

			ReceiptItemEntity trashedItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			trashedItem.Id = trashedItemId;
			trashedItem.NormalizedDescriptionId = discardId;
			// Soft-deleted: still a real row, still restorable from the recycle bin.
			trashedItem.DeletedAt = DateTimeOffset.UtcNow;

			setup.ReceiptItems.AddRange(liveItem, trashedItem);
			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = CreateService();

		// Act
		int relinked = await service.MergeAsync(keepId, discardId, CancellationToken.None);

		// Assert — the count contract is "live items re-linked", so the trashed row must not
		// inflate it even though it is repointed too.
		relinked.Should().Be(1, "the returned count reports live re-linked items only");

		await using ApplicationDbContext verify = fixture.CreateDbContext();

		(await verify.NormalizedDescriptions.AnyAsync(e => e.Id == discardId))
			.Should().BeFalse("the discarded canonical row is deleted by the merge");

		ReceiptItemEntity liveAfter = await verify.ReceiptItems
			.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.FirstAsync(r => r.Id == liveItemId);
		liveAfter.NormalizedDescriptionId.Should().Be(keepId);

		ReceiptItemEntity trashedAfter = await verify.ReceiptItems
			.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.FirstAsync(r => r.Id == trashedItemId);
		trashedAfter.NormalizedDescriptionId.Should().Be(
			keepId,
			"a soft-deleted item must survive the merge with its canonical link intact — "
			+ "SetNull would strand it unlinked once restored from the recycle bin");
	}

	[Fact]
	public async Task MergeAsync_LeavesNoReceiptItemPointingAtTheDeletedRow()
	{
		// Arrange — three trashed items and no live ones at all: the re-link query sees nothing
		// through the default filter, so today the merge reports success having repointed zero
		// rows while Postgres quietly nulls all three.
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		List<Guid> trashedIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Receipts.Add(receipt);
			setup.NormalizedDescriptions.AddRange(
				BuildNormalized(keepId, "Cheddar Cheese"),
				BuildNormalized(discardId, "Chedar Chese"));
			await setup.SaveChangesAsync();

			foreach (Guid id in trashedIds)
			{
				ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
				item.Id = id;
				item.NormalizedDescriptionId = discardId;
				item.DeletedAt = DateTimeOffset.UtcNow;
				setup.ReceiptItems.Add(item);
			}

			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = CreateService();

		// Act
		int relinked = await service.MergeAsync(keepId, discardId, CancellationToken.None);

		// Assert
		relinked.Should().Be(0, "no live items existed to re-link");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<Guid?> linkedIds = await verify.ReceiptItems
			.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking()
			.Where(r => trashedIds.Contains(r.Id))
			.Select(r => r.NormalizedDescriptionId)
			.ToListAsync();

		linkedIds.Should().OnlyContain(
			id => id == keepId,
			"every trashed item must be repointed at the surviving row rather than SetNull'd");
	}

	// RECEIPTS-892. The score on a re-linked item was the similarity to the DISCARDED row, so a
	// threshold-impact preview run after a merge was computed partly from comparisons against a
	// row that no longer existed. Re-scoring has to keep those items inside the scored
	// population — nulling the score instead would move them to Unresolved permanently, since
	// the resolver only picks up rows WHERE "NormalizedDescriptionId" IS NULL.
	//
	// Postgres-only: the re-score runs the real pgvector `<=>` expression, which InMemory cannot.
	[Fact]
	public async Task MergeAsync_LeavesThresholdImpactCountsStable()
	{
		Guid keepId = Guid.NewGuid();
		Guid discardId = Guid.NewGuid();
		Guid itemId = Guid.NewGuid();

		float[] vector = UnitVector();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Receipts.Add(receipt);

			// Names unique to this test: the fixture's Postgres is shared across the collection
			// and NormalizedDescriptions has a unique index on lower(CanonicalName).
			NormalizedDescriptionEntity keep = BuildNormalized(keepId, "Threshold Sourdough");
			keep.Embedding = new Vector(vector);
			setup.NormalizedDescriptions.AddRange(keep, BuildNormalized(discardId, "Thrshold Sourdogh"));
			await setup.SaveChangesAsync();

			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item.Id = itemId;
			item.NormalizedDescriptionId = discardId;
			// The similarity to "Thrshold Sourdogh" — high enough to be auto-accepted, and about
			// to stop describing anything real.
			item.NormalizedDescriptionMatchScore = 0.97;
			setup.ReceiptItems.Add(item);
			await setup.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(
			new FixtureContextFactory(fixture),
			new FixedVectorEmbeddingService(vector),
			new NormalizedDescriptionMapper(),
			new NormalizedDescriptionSettingsMapper());

		ThresholdImpactPreview before = await service.PreviewThresholdImpactAsync(
			NormalizedDescriptionService.InitialAutoAcceptThreshold,
			NormalizedDescriptionService.InitialPendingReviewThreshold,
			CancellationToken.None);

		await service.MergeAsync(keepId, discardId, CancellationToken.None);

		ThresholdImpactPreview after = await service.PreviewThresholdImpactAsync(
			NormalizedDescriptionService.InitialAutoAcceptThreshold,
			NormalizedDescriptionService.InitialPendingReviewThreshold,
			CancellationToken.None);

		after.Current.Unresolved.Should().Be(
			before.Current.Unresolved,
			"a merge must not push its items out of the scored population");
		after.Current.AutoAccepted.Should().Be(
			before.Current.AutoAccepted,
			"the item is still a strong match — measured against the survivor this time");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity itemAfter = await verify.ReceiptItems
			.IgnoreAutoIncludes().AsNoTracking()
			.FirstAsync(r => r.Id == itemId);

		itemAfter.NormalizedDescriptionId.Should().Be(keepId);
		// Same vector on both sides, so the real cosine similarity is 1 — and crucially NOT the
		// 0.97 that was measured against the row this merge deleted.
		itemAfter.NormalizedDescriptionMatchScore.Should().NotBeNull();
		itemAfter.NormalizedDescriptionMatchScore!.Value.Should().BeApproximately(1.0, 1e-6);
	}

	private static float[] UnitVector()
	{
		float[] vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[0] = 1f;
		return vector;
	}

	private NormalizedDescriptionService CreateService() => new(
		new FixtureContextFactory(fixture),
		new UnconfiguredEmbeddingService(),
		new NormalizedDescriptionMapper(),
		new NormalizedDescriptionSettingsMapper());

	private static NormalizedDescriptionEntity BuildNormalized(Guid id, string canonicalName) => new()
	{
		Id = id,
		CanonicalName = canonicalName,
		Status = DomainStatus.Active,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	// Merge re-scores re-linked items against the surviving row (RECEIPTS-892), so it *does*
	// embed now. An unconfigured stub keeps the link-integrity tests above off the ONNX model:
	// with no embedding service the re-score resolves to a null score, which is the documented
	// fallback and does not affect what those tests assert (the link, not the number).
	private sealed class UnconfiguredEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => false;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			Task.FromResult(Array.Empty<float>());

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) =>
			Task.FromResult(new List<float[]>());
	}

	// Returns one fixed vector for any text, so a row seeded with the same vector scores an
	// exact cosine similarity of 1. Deterministic, and still exercises the real pgvector
	// `<=>` expression rather than stubbing the similarity out.
	private sealed class FixedVectorEmbeddingService(float[] vector) : IEmbeddingService
	{
		public bool IsConfigured => true;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			Task.FromResult(vector);

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) =>
			Task.FromResult(texts.Select(_ => vector).ToList());
	}
}
