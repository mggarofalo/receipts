using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

/// <summary>
/// Promotes consistent Pending review names to Active in one previewed, undoable operation
/// (RECEIPTS-1107, docs/decisions/2026-10-31-promote-consistent-pending-names.md). It approves names,
/// not purchases: links and their review state are untouched, and no scoped mapping is created.
/// </summary>
public sealed class PendingNamePromotionService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ICommittedChangePublisher committedChangePublisher) : IPendingNamePromotionService
{
	/// <summary>A second purchase under the same name is the first evidence that it is reused.</summary>
	internal const int MinimumPurchases = 2;

	private const int HistoryLimit = 20;

	public async Task<PendingNamePromotionPreview> PreviewAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		List<PromotableName> names = await EligibleAsync(context, cancellationToken);
		return Preview(names);
	}

	public async Task<PendingNamePromotionResult?> PromoteAsync(string actor, string expectedFingerprint, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);

		// Identities, not counts: the admin confirmed the set they were shown.
		List<PromotableName> names = await EligibleAsync(context, cancellationToken);
		PendingNamePromotionPreview preview = Preview(names);
		if (!string.Equals(preview.Fingerprint, expectedFingerprint, StringComparison.Ordinal)) { return null; }
		if (names.Count == 0) { return new PendingNamePromotionResult(null, 0, 0); }

		Guid[] eligible = names.Select(name => name.Id).ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, eligible, cancellationToken);
		List<NormalizedDescriptionEntity> targets = await context.NormalizedDescriptions
			.Where(target => eligible.Contains(target.Id) && target.Status == NormalizedDescriptionStatus.PendingReview)
			.ToListAsync(cancellationToken);
		foreach (NormalizedDescriptionEntity target in targets) { target.Status = NormalizedDescriptionStatus.Active; }
		await context.SaveChangesAsync(cancellationToken);
		// Record only the names this transaction changed, so undo can never "restore" one it did not promote.
		Guid[] ids = targets.Select(target => target.Id).ToArray();

		// The revision trigger has issued each promoted name a new revision; undo compares against it.
		Dictionary<Guid, Guid> revisions = await context.NormalizedDescriptions.AsNoTracking()
			.Where(target => ids.Contains(target.Id))
			.ToDictionaryAsync(target => target.Id, target => target.NormalizationRevision, cancellationToken);
		PendingNamePromotionEntity promotion = new()
		{
			Id = Guid.NewGuid(),
			CreatedBy = actor,
			CreatedAt = DateTimeOffset.UtcNow,
			MinimumPurchases = MinimumPurchases,
			PurchaseCount = names.Where(name => ids.Contains(name.Id)).Sum(name => name.PurchaseCount),
			Entries = ids.Select(id => new PendingNamePromotionEntryEntity { NormalizedDescriptionId = id, RevisionAfter = revisions[id] }).ToList(),
		};
		context.PendingNamePromotions.Add(promotion);
		// One entry for the operator's decision; each status change is audited automatically as well.
		context.AddSemanticAuditEntry(
			NormalizedDescriptionService.NormalizedDescriptionEntityType,
			string.Empty,
			AuditAction.Update,
			[
				new FieldChange { FieldName = "operation", OldValue = null, NewValue = "PromotePendingNames" },
				new FieldChange { FieldName = "promotionId", OldValue = null, NewValue = promotion.Id.ToString() },
				new FieldChange { FieldName = "promotedCount", OldValue = null, NewValue = ids.Length.ToString() },
				new FieldChange { FieldName = "purchaseCount", OldValue = null, NewValue = promotion.PurchaseCount.ToString() },
				new FieldChange { FieldName = "minimumPurchases", OldValue = null, NewValue = MinimumPurchases.ToString() },
			],
			promotion.CreatedAt);
		await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		await committedChangePublisher.PublishAsync(new(CommittedEntityType.NormalizedDescription, CommittedChangeType.Updated));
		return new PendingNamePromotionResult(promotion.Id, ids.Length, promotion.PurchaseCount);
	}

	public async Task<IReadOnlyList<PendingNamePromotion>> ListAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await context.PendingNamePromotions.AsNoTracking()
			.OrderByDescending(promotion => promotion.CreatedAt).ThenBy(promotion => promotion.Id)
			.Take(HistoryLimit)
			.Select(promotion => new PendingNamePromotion(promotion.Id, promotion.CreatedBy, promotion.CreatedAt,
				promotion.Entries.Count, promotion.PurchaseCount, promotion.UndoneAt, promotion.UndoneBy,
				promotion.RestoredCount, promotion.KeptCount))
			.ToListAsync(cancellationToken);
	}

	public async Task<PendingNamePromotionUndo> UndoAsync(string actor, Guid promotionId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		PendingNamePromotionEntity? promotion = await context.PendingNamePromotions.Include(value => value.Entries)
			.SingleOrDefaultAsync(value => value.Id == promotionId, cancellationToken);
		if (promotion is null) { return new(PendingNamePromotionUndoOutcome.NotFound, 0, 0); }
		if (promotion.UndoneAt is not null) { return new(PendingNamePromotionUndoOutcome.AlreadyUndone, 0, 0); }

		Guid[] ids = promotion.Entries.Select(entry => entry.NormalizedDescriptionId).ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, ids, cancellationToken);
		Dictionary<Guid, NormalizedDescriptionEntity> names = await context.NormalizedDescriptions
			.Where(target => ids.Contains(target.Id)).ToDictionaryAsync(target => target.Id, cancellationToken);
		HashSet<Guid> mapped = (await context.NormalizationSourceMappings
			.Where(mapping => mapping.State == "active" && mapping.TargetId != null && ids.Contains(mapping.TargetId.Value))
			.Select(mapping => mapping.TargetId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();
		// A verified purchase is a later human decision; returning its name to review would reset it.
		HashSet<Guid> verified = (await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => item.NormalizedDescriptionId != null && ids.Contains(item.NormalizedDescriptionId.Value)
				&& item.NormalizationReviewState == NormalizationReviewState.Verified)
			.Select(item => item.NormalizedDescriptionId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();

		foreach (PendingNamePromotionEntryEntity entry in promotion.Entries)
		{
			entry.Restored = names.TryGetValue(entry.NormalizedDescriptionId, out NormalizedDescriptionEntity? name)
				&& name.Status == NormalizedDescriptionStatus.Active
				&& name.NormalizationRevision == entry.RevisionAfter
				&& !mapped.Contains(name.Id)
				&& !verified.Contains(name.Id);
			if (entry.Restored == true) { name!.Status = NormalizedDescriptionStatus.PendingReview; }
		}
		promotion.UndoneAt = DateTimeOffset.UtcNow;
		promotion.UndoneBy = actor;
		promotion.RestoredCount = promotion.Entries.Count(entry => entry.Restored == true);
		promotion.KeptCount = promotion.Entries.Count - promotion.RestoredCount;
		context.AddSemanticAuditEntry(
			NormalizedDescriptionService.NormalizedDescriptionEntityType,
			string.Empty,
			AuditAction.Update,
			[
				new FieldChange { FieldName = "operation", OldValue = null, NewValue = "UndoPendingNamePromotion" },
				new FieldChange { FieldName = "promotionId", OldValue = null, NewValue = promotion.Id.ToString() },
				new FieldChange { FieldName = "restoredCount", OldValue = null, NewValue = promotion.RestoredCount.ToString() },
				new FieldChange { FieldName = "keptCount", OldValue = null, NewValue = promotion.KeptCount.ToString() },
			],
			promotion.UndoneAt.Value);
		await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		await committedChangePublisher.PublishAsync(new(CommittedEntityType.NormalizedDescription, CommittedChangeType.Updated));
		return new(PendingNamePromotionUndoOutcome.Undone, promotion.RestoredCount.Value, promotion.KeptCount.Value);
	}

	private static PendingNamePromotionPreview Preview(List<PromotableName> names) => new(
		MinimumPurchases,
		names.Sum(name => name.PurchaseCount),
		NormalizedDescriptionService.ComputePendingFingerprint(names.Select(name => name.Id)),
		names);

	/// <summary>
	/// Live purchases only. A source key is the receipt's location, the description and the item code,
	/// each trimmed of outer spaces as <c>NormalizationCatalogPolicy.Normalize</c> trims them.
	/// </summary>
	private static async Task<List<PromotableName>> EligibleAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql()) { return []; }
		List<EligibleRow> rows = await context.Database.SqlQuery<EligibleRow>($"""
			WITH live AS (
				SELECT i."NormalizedDescriptionId" AS target, btrim(r."Location", ' ') AS location,
					btrim(i."Description", ' ') AS description, NULLIF(btrim(i."ReceiptItemCode", ' '), '') AS code
				FROM receipts."ReceiptItems" i JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
				WHERE i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL AND i."NormalizedDescriptionId" IS NOT NULL),
			shared AS (
				SELECT location, description, code FROM live GROUP BY location, description, code
				HAVING count(DISTINCT target) > 1),
			-- [:alnum:] follows the database locale; the deployed UTF-8 locale keeps non-ASCII letters.
			letters AS (
				SELECT "Id", regexp_replace(lower("CanonicalName"), '[^[:alnum:]]', '', 'g') AS letters
				FROM matching."NormalizedDescriptions")
			SELECT d."Id", d."CanonicalName", count(*)::int AS "PurchaseCount"
			FROM matching."NormalizedDescriptions" d JOIN live l ON l.target = d."Id"
			WHERE d."Status" = 'PendingReview'
				AND NOT EXISTS (SELECT 1 FROM live s JOIN shared k ON k.location = s.location AND k.description = s.description
					AND k.code IS NOT DISTINCT FROM s.code WHERE s.target = d."Id")
				AND NOT EXISTS (SELECT 1 FROM letters a JOIN letters b ON b.letters = a.letters AND b."Id" <> a."Id" WHERE a."Id" = d."Id")
			GROUP BY d."Id", d."CanonicalName"
			HAVING count(*) >= {MinimumPurchases}
			ORDER BY d."CanonicalName", d."Id"
			""").ToListAsync(cancellationToken);
		return rows.Select(row => new PromotableName(row.Id, row.CanonicalName, row.PurchaseCount)).ToList();
	}

	private sealed class EligibleRow
	{
		public Guid Id { get; set; }
		public string CanonicalName { get; set; } = "";
		public int PurchaseCount { get; set; }
	}
}
