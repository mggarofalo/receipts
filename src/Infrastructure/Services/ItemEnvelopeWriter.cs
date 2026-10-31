using Application.Envelopes;
using Domain.Envelopes;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Records each item's envelope at the persistence chokepoints, using
/// <see cref="EnvelopeDerivationPolicy"/> as the single derivation rule.
/// </summary>
internal static class ItemEnvelopeWriter
{
	/// <summary>New items: override, else the receipt's paid-from, else default (filled when typed).</summary>
	internal static async Task PrepareNewItemsAsync(ApplicationDbContext db, IReadOnlyList<ReceiptItemEntity> items,
		ReceiptEntity? newReceipt, CancellationToken ct)
	{
		// A new receipt's choices were just checked. An existing receipt's may have been archived since:
		// those are skipped, as archived defaults are, so the item falls back to its default.
		Dictionary<Guid, (Guid? PaidFrom, Guid? Occasion)> receipts = newReceipt is not null
			? new() { [newReceipt.Id] = (newReceipt.PaidFromEnvelopeId, newReceipt.OccasionId) }
			: await SelectableReceiptChoicesAsync(db, items.Select(item => item.ReceiptId).Distinct().ToArray(), ct);
		await RequireChoicesAsync(db, items.Select(item => item.EnvelopeOverrideId), items.Select(item => item.OccasionId), ct);
		foreach (ReceiptItemEntity item in items)
		{
			(Guid? paidFrom, Guid? occasion) = receipts.GetValueOrDefault(item.ReceiptId);
			Set(item, EnvelopeDerivationPolicy.ForNewItem(item.EnvelopeOverrideId, paidFrom, null));
			item.OccasionId ??= occasion;
		}
	}

	/// <summary>An ordinary edit keeps the recorded envelope and occasion unless it chooses new ones.</summary>
	internal static async Task ApplyEditsAsync(ApplicationDbContext db,
		IReadOnlyList<(ReceiptItemEntity Incoming, ReceiptItemEntity Stored)> edits, CancellationToken ct)
	{
		// Only new choices must be selectable; keeping an archived envelope or occasion is allowed.
		await RequireChoicesAsync(db,
			edits.Where(edit => !(edit.Stored.EnvelopeSource == EnvelopeSource.Item && edit.Stored.EnvelopeId == edit.Incoming.EnvelopeOverrideId))
				.Select(edit => edit.Incoming.EnvelopeOverrideId),
			edits.Where(edit => edit.Incoming.OccasionId != edit.Stored.OccasionId).Select(edit => edit.Incoming.OccasionId), ct);
		foreach ((ReceiptItemEntity incoming, ReceiptItemEntity stored) in edits)
		{
			Set(stored, EnvelopeDerivationPolicy.ForEditedItem(Get(stored), incoming.EnvelopeOverrideId));
			stored.OccasionId = incoming.OccasionId ?? stored.OccasionId;
		}
	}

	/// <summary>
	/// Records an item's explicit choices: an envelope is a per-item override and null returns the item to
	/// automatic (the receipt's paid-from, else its item type's default); a null occasion clears the tag.
	/// </summary>
	internal static async Task ApplyItemChoicesAsync(ApplicationDbContext db, ReceiptItemEntity item, Guid? envelopeOverride,
		Guid? occasion, CancellationToken ct)
	{
		EnvelopeAssignment stored = Get(item);
		// Only new choices must be selectable; keeping an archived envelope or occasion is allowed.
		await RequireChoicesAsync(db,
			[stored.Source == EnvelopeSource.Item && stored.EnvelopeId == envelopeOverride ? null : envelopeOverride],
			[occasion != item.OccasionId ? occasion : null], ct);
		item.OccasionId = occasion;
		if (envelopeOverride is null && stored.Source != EnvelopeSource.Item)
		{
			return;
		}
		Guid? paidFrom = null;
		Guid? itemTypeDefault = null;
		if (envelopeOverride is null)
		{
			paidFrom = (await SelectableReceiptChoicesAsync(db, [item.ReceiptId], ct)).GetValueOrDefault(item.ReceiptId).PaidFrom;
			if (paidFrom is null && (await CurrentTypesAsync(db, [item.Id], ct)).TryGetValue(item.Id, out var type))
			{
				itemTypeDefault = await ResolveDefaultAsync(db, type.CategoryId, type.RevisionId, ct);
			}
		}
		Set(item, EnvelopeDerivationPolicy.AfterOverrideChange(stored, envelopeOverride, paidFrom, itemTypeDefault));
	}

	/// <summary>
	/// Every classification write passes here: default-sourced or unresolved envelopes follow the
	/// new item type. The caller saves the tracked item with its assignment.
	/// </summary>
	internal static async Task AfterItemTypeChangeAsync(ApplicationDbContext db, Guid itemId, Guid? categoryId,
		Guid revisionId, CancellationToken ct)
	{
		ReceiptItemEntity item = db.ReceiptItems.Local.FirstOrDefault(row => row.Id == itemId)
			?? await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().SingleAsync(row => row.Id == itemId, ct);
		EnvelopeAssignment stored = Get(item);
		if (!EnvelopeDerivationPolicy.FollowsItemType(stored))
		{
			return;
		}
		Set(item, EnvelopeDerivationPolicy.AfterItemTypeChange(stored, await ResolveDefaultAsync(db, categoryId, revisionId, ct)));
	}

	/// <summary>Records a receipt's paid-from envelope and occasion (null clears) and applies a change to its items.</summary>
	internal static async Task ApplyReceiptChoicesAsync(ApplicationDbContext db, ReceiptEntity receipt, Guid? paidFrom,
		Guid? occasion, CancellationToken ct)
	{
		Guid? previousPaidFrom = receipt.PaidFromEnvelopeId;
		Guid? previousOccasion = receipt.OccasionId;
		// Only new choices must be selectable; keeping an archived envelope or occasion is allowed.
		await RequireChoicesAsync(db, [paidFrom != previousPaidFrom ? paidFrom : null],
			[occasion != previousOccasion ? occasion : null], ct);
		receipt.PaidFromEnvelopeId = paidFrom;
		receipt.OccasionId = occasion;
		await AfterReceiptChoiceChangeAsync(db, receipt, previousPaidFrom, previousOccasion, ct);
	}

	/// <summary>Applies a changed receipt paid-from envelope and occasion to its items.</summary>
	internal static async Task AfterReceiptChoiceChangeAsync(ApplicationDbContext db, ReceiptEntity receipt,
		Guid? previousPaidFrom, Guid? previousOccasion, CancellationToken ct)
	{
		bool paidFromChanged = receipt.PaidFromEnvelopeId != previousPaidFrom;
		bool occasionChanged = receipt.OccasionId != previousOccasion;
		if (!paidFromChanged && !occasionChanged)
		{
			return;
		}
		List<ReceiptItemEntity> items = await db.ReceiptItems.IgnoreAutoIncludes()
			.Where(item => item.ReceiptId == receipt.Id).ToListAsync(ct);
		Dictionary<Guid, (Guid? CategoryId, Guid RevisionId)> types = paidFromChanged && receipt.PaidFromEnvelopeId is null
			? await CurrentTypesAsync(db, items.Select(item => item.Id).ToArray(), ct) : [];
		foreach (ReceiptItemEntity item in items)
		{
			if (paidFromChanged)
			{
				Guid? itemTypeDefault = types.TryGetValue(item.Id, out var type)
					? await ResolveDefaultAsync(db, type.CategoryId, type.RevisionId, ct) : null;
				Set(item, EnvelopeDerivationPolicy.AfterPaidFromChange(Get(item), receipt.PaidFromEnvelopeId, itemTypeDefault));
			}
			// The receipt occasion is a convenience: it replaces only items that carried the previous one.
			if (occasionChanged && item.OccasionId == previousOccasion)
			{
				item.OccasionId = receipt.OccasionId;
			}
		}
	}

	/// <summary>Rejects choices of a missing or archived envelope or occasion.</summary>
	internal static async Task RequireChoicesAsync(ApplicationDbContext db, IEnumerable<Guid?> envelopeIds,
		IEnumerable<Guid?> occasionIds, CancellationToken ct)
	{
		Guid[] envelopes = envelopeIds.OfType<Guid>().Distinct().ToArray();
		if (envelopes.Length > 0 && await db.Envelopes.CountAsync(row => envelopes.Contains(row.Id) && row.IsActive, ct) != envelopes.Length)
		{
			throw new EnvelopeException(400, "Envelope must exist and be active.");
		}
		Guid[] occasions = occasionIds.OfType<Guid>().Distinct().ToArray();
		if (occasions.Length > 0 && await db.Occasions.CountAsync(row => occasions.Contains(row.Id) && row.IsActive, ct) != occasions.Length)
		{
			throw new EnvelopeException(400, "Occasion must exist and be active.");
		}
	}

	internal static async Task<Guid?> ResolveDefaultAsync(ApplicationDbContext db, Guid? categoryId, Guid revisionId, CancellationToken ct)
	{
		if (categoryId is not Guid category)
		{
			return null;
		}
		// An archived envelope cannot be chosen, so its defaults are skipped (the item stays unresolved
		// or falls back to a mapped ancestor) until the envelope is restored or the default changes.
		Dictionary<Guid, Guid> defaults = await (from row in db.ItemTypeEnvelopeDefaults.AsNoTracking()
												 join envelope in db.Envelopes on row.EnvelopeId equals envelope.Id
												 where envelope.IsActive
												 select row).ToDictionaryAsync(row => row.CategoryId, row => row.EnvelopeId, ct);
		if (defaults.Count == 0)
		{
			return null;
		}
		Dictionary<Guid, TaxonomyNode> nodes = await db.TaxonomyNodes.AsNoTracking().Where(node => node.RevisionId == revisionId)
			.ToDictionaryAsync(node => node.CategoryId, node => new TaxonomyNode(node.CategoryId, node.ParentId, node.Name, node.Definition, node.Archived), ct);
		return EnvelopeDerivationPolicy.ResolveDefault(category, nodes, defaults);
	}

	private static async Task<Dictionary<Guid, (Guid? CategoryId, Guid RevisionId)>> CurrentTypesAsync(ApplicationDbContext db,
		Guid[] itemIds, CancellationToken ct)
		=> await (from current in db.CurrentClassifications.AsNoTracking()
				  join assignment in db.Classifications.AsNoTracking() on current.AssignmentId equals assignment.Id
				  where itemIds.Contains(current.ReceiptItemId)
				  select new { current.ReceiptItemId, assignment.CategoryId, assignment.TaxonomyRevisionId })
			.ToDictionaryAsync(row => row.ReceiptItemId, row => (row.CategoryId, row.TaxonomyRevisionId), ct);

	/// <summary>Each receipt's paid-from envelope and occasion, or null where the choice is archived.</summary>
	private static async Task<Dictionary<Guid, (Guid? PaidFrom, Guid? Occasion)>> SelectableReceiptChoicesAsync(
		ApplicationDbContext db, Guid[] receiptIds, CancellationToken ct)
		=> await (from receipt in db.Receipts.IgnoreQueryFilters().AsNoTracking()
				  where receiptIds.Contains(receipt.Id)
				  select new
				  {
					  receipt.Id,
					  PaidFrom = db.Envelopes.Any(envelope => envelope.Id == receipt.PaidFromEnvelopeId && envelope.IsActive)
						  ? receipt.PaidFromEnvelopeId : null,
					  Occasion = db.Occasions.Any(occasion => occasion.Id == receipt.OccasionId && occasion.IsActive)
						  ? receipt.OccasionId : null,
				  })
			.ToDictionaryAsync(row => row.Id, row => (row.PaidFrom, row.Occasion), ct);

	private static EnvelopeAssignment Get(ReceiptItemEntity item) => new(item.EnvelopeId, item.EnvelopeSource);

	private static void Set(ReceiptItemEntity item, EnvelopeAssignment assignment)
	{
		item.EnvelopeId = assignment.EnvelopeId;
		item.EnvelopeSource = assignment.Source;
	}
}
