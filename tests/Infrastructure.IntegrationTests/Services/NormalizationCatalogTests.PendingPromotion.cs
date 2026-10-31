using Application.Models.NormalizedDescriptions;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

// RECEIPTS-1107: promote consistent Pending review names in one previewed, undoable action.
public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task PendingPromotion_OffersOnlyConsistentNamesWithTwoPurchases()
	{
		await ResetAsync();
		var consistent = await PendingAsync("Oat milk");
		await ItemsAsync(new("Store A", "OAT MILK 64OZ", null), 2, "legacy_unknown", consistent.Id);
		var once = await PendingAsync("Rye bread");
		await ItemsAsync(new("Store A", "RYE BREAD", null), 1, "legacy_unknown", once.Id);
		var active = await CanonicalAsync("Butter");
		await ItemsAsync(new("Store A", "BUTTER", null), 3, "legacy_unknown", active.Id);
		// One source key linked to two names: neither is consistent.
		var shared = await PendingAsync("Cheddar");
		var rival = await PendingAsync("Sharp cheddar");
		await ItemsAsync(new("Store B", "CHEDDAR 2LB", "77"), 3, "legacy_unknown", shared.Id);
		await ItemsAsync(new("Store B", "CHEDDAR 2LB", "77"), 1, "legacy_unknown", rival.Id);
		// Same letters and digits as an existing name.
		var collides = await PendingAsync("Butter!");
		await ItemsAsync(new("Store C", "BUTTER SALTED", null), 2, "legacy_unknown", collides.Id);

		var preview = await PromotionService().PreviewAsync(CancellationToken.None);

		preview.MinimumPurchases.Should().Be(2);
		preview.Names.Should().ContainSingle().Which.Should().Be(new PromotableName(consistent.Id, "Oat milk", 2));
		preview.PurchaseCount.Should().Be(2);
	}

	[Fact]
	public async Task PendingPromotion_RefusesAStalePreview()
	{
		await ResetAsync();
		var name = await PendingAsync("Oat milk");
		await ItemsAsync(new("Store A", "OAT MILK 64OZ", null), 2, "legacy_unknown", name.Id);
		var stale = await PromotionService().PreviewAsync(CancellationToken.None);
		var later = await PendingAsync("Rye bread");
		await ItemsAsync(new("Store A", "RYE BREAD", null), 2, "legacy_unknown", later.Id);

		var result = await PromotionService().PromoteAsync(Actor, stale.Fingerprint, CancellationToken.None);

		result.Should().BeNull();
		(await StatusAsync(name.Id)).Should().Be(NormalizedDescriptionStatus.PendingReview);
		await using var db = fixture.CreateDbContext();
		(await db.PendingNamePromotions.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task PendingPromotion_MakesNamesReachableWithoutTouchingPurchases()
	{
		await ResetAsync();
		var name = await PendingAsync("Oat milk");
		var items = await ItemsAsync(new("Store A", "OAT MILK 64OZ", null), 2, "legacy_unknown", name.Id);
		var chooser = new ReceiptNormalizationCandidatesService(new Factory(fixture), TimeProvider.System);
		NormalizationSourceKey source = new("Store A", "Oat milk", null);
		(await chooser.GetCandidatesAsync(Actor, source, CancellationToken.None)).Should().BeEmpty("retrieval offers only Active names");
		var before = await ReadAsync(items);
		var preview = await PromotionService().PreviewAsync(CancellationToken.None);

		var result = await PromotionService().PromoteAsync(Actor, preview.Fingerprint, CancellationToken.None);

		result.Should().NotBeNull();
		result!.PromotedCount.Should().Be(1);
		result.PurchaseCount.Should().Be(2);
		(await StatusAsync(name.Id)).Should().Be(NormalizedDescriptionStatus.Active);
		(await chooser.GetCandidatesAsync(Actor, source, CancellationToken.None)).Should().ContainSingle(candidate => candidate.Id == name.Id);
		(await ReadAsync(items)).Select(item => new { item.NormalizedDescriptionId, item.NormalizationOrigin, item.NormalizationReviewState })
			.Should().BeEquivalentTo(before.Select(item => new { item.NormalizedDescriptionId, item.NormalizationOrigin, item.NormalizationReviewState }),
				"promotion approves names, not purchases");
		var history = await PromotionService().ListAsync(CancellationToken.None);
		history.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = result.PromotionId!.Value, CreatedBy = Actor, PromotedCount = 1, PurchaseCount = 2, UndoneAt = (DateTimeOffset?)null });
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationSourceMappings.CountAsync()).Should().Be(0, "no scoped mapping is created");
		(await db.AuditLogs.AnyAsync(entry => entry.EntityType == "NormalizedDescription" && entry.ChangesJson.Contains("PromotePendingNames")
			&& entry.ChangesJson.Contains(result.PromotionId!.Value.ToString()))).Should().BeTrue();
	}

	[Fact]
	public async Task PendingPromotion_UndoRestoresUnchangedNamesAndKeepsChangedOnes()
	{
		await ResetAsync();
		var unchanged = await PendingAsync("Oat milk");
		await ItemsAsync(new("Store A", "OAT MILK 64OZ", null), 2, "legacy_unknown", unchanged.Id);
		var renamed = await PendingAsync("Rye bread");
		await ItemsAsync(new("Store A", "RYE BREAD", null), 2, "legacy_unknown", renamed.Id);
		var mapped = await PendingAsync("Butter");
		await ItemsAsync(new("Store A", "BUTTER", null), 2, "legacy_unknown", mapped.Id);
		var merged = await PendingAsync("Eggs");
		await ItemsAsync(new("Store A", "EGGS 12CT", null), 2, "legacy_unknown", merged.Id);
		var preview = await PromotionService().PreviewAsync(CancellationToken.None);
		var promoted = await PromotionService().PromoteAsync(Actor, preview.Fingerprint, CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizedDescriptions.SingleAsync(row => row.Id == renamed.Id)).DisplayLabel = "Rye loaf";
			db.NormalizationSourceMappings.Add(new NormalizationSourceMappingEntity
			{
				Id = Guid.NewGuid(),
				SourceHash = NormalizationCatalogPolicy.Hash(new("Store A", "BUTTER", null)),
				Location = "Store A",
				Description = "BUTTER",
				TargetId = mapped.Id,
				ApprovedBy = Actor,
				ApprovedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"""
				UPDATE receipts."ReceiptItems" SET "NormalizedDescriptionId" = {unchanged.Id} WHERE "NormalizedDescriptionId" = {merged.Id};
				DELETE FROM matching."NormalizedDescriptions" WHERE "Id" = {merged.Id};
				""");
		}

		var undo = await PromotionService().UndoAsync(Actor, promoted!.PromotionId!.Value, CancellationToken.None);

		undo.Should().Be(new PendingNamePromotionUndo(PendingNamePromotionUndoOutcome.Undone, 1, 3));
		(await StatusAsync(unchanged.Id)).Should().Be(NormalizedDescriptionStatus.PendingReview);
		(await StatusAsync(renamed.Id)).Should().Be(NormalizedDescriptionStatus.Active);
		(await StatusAsync(mapped.Id)).Should().Be(NormalizedDescriptionStatus.Active);
		(await PromotionService().ListAsync(CancellationToken.None)).Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new { UndoneBy = Actor, RestoredCount = (int?)1, KeptCount = (int?)3 });
		(await PromotionService().UndoAsync(Actor, promoted.PromotionId.Value, CancellationToken.None)).Outcome
			.Should().Be(PendingNamePromotionUndoOutcome.AlreadyUndone);
		(await PromotionService().UndoAsync(Actor, Guid.NewGuid(), CancellationToken.None)).Outcome
			.Should().Be(PendingNamePromotionUndoOutcome.NotFound);
	}

	[Fact]
	public async Task PendingPromotion_UndoKeepsANameWithAVerifiedPurchase()
	{
		await ResetAsync();
		var name = await PendingAsync("Oat milk");
		var items = await ItemsAsync(new("Store A", "OAT MILK 64OZ", null), 2, "legacy_unknown", name.Id);
		var preview = await PromotionService().PreviewAsync(CancellationToken.None);
		var promoted = await PromotionService().PromoteAsync(Actor, preview.Fingerprint, CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			// A reviewed purchase, written the way the review path prepares it past the evidence guard.
			string prepared = $",{items[0]},";
			await using var transaction = await db.Database.BeginTransactionAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"""
				SELECT set_config('receipts.review_prepared', {prepared}, true);
				UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'Verified', "ReviewedTargetId" = {name.Id},
					"ReviewedBy" = {Actor}, "ReviewedAt" = now() WHERE "Id" = {items[0]};
				""");
			await transaction.CommitAsync();
		}

		var undo = await PromotionService().UndoAsync(Actor, promoted!.PromotionId!.Value, CancellationToken.None);

		undo.Should().Be(new PendingNamePromotionUndo(PendingNamePromotionUndoOutcome.Undone, 0, 1));
		(await StatusAsync(name.Id)).Should().Be(NormalizedDescriptionStatus.Active);
		(await ReadAsync(items)).Should().Contain(item => item.NormalizationReviewState == NormalizationReviewState.Verified,
			"undo must not reset a later review decision");
	}

	private PendingNamePromotionService PromotionService() => new(new Factory(fixture), publisher.Object);

	private async Task<NormalizedDescriptionEntity> PendingAsync(string name)
	{
		await using var db = fixture.CreateDbContext();
		var entity = new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = name, Status = NormalizedDescriptionStatus.PendingReview, CreatedAt = DateTimeOffset.UtcNow };
		db.NormalizedDescriptions.Add(entity);
		await db.SaveChangesAsync();
		return entity;
	}

	private async Task<NormalizedDescriptionStatus?> StatusAsync(Guid id)
	{
		await using var db = fixture.CreateDbContext();
		return (await db.NormalizedDescriptions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id))?.Status;
	}
}
