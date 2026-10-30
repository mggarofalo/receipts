using Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class TrashService(ApplicationDbContext context) : ITrashService
{
	public async Task<PurgeTrashResult> PurgeAllDeletedAsync(CancellationToken cancellationToken)
	{
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

		await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		// History protects the entire aggregate, including previously trashed siblings.
		Guid[] retainedReceiptIds = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => context.Classifications.Any(assignment => assignment.ReceiptItemId == item.Id))
			.Select(item => item.ReceiptId).Distinct().ToArrayAsync(cancellationToken);
		// Preserve both ends of duplicate annotations to avoid cascade-erasing retained records.
		while (true)
		{
			var pairs = await context.AcceptedDuplicatePairs.IgnoreQueryFilters()
				.Where(pair => retainedReceiptIds.Contains(pair.ReceiptIdA) || retainedReceiptIds.Contains(pair.ReceiptIdB))
				.Select(pair => new { pair.ReceiptIdA, pair.ReceiptIdB }).ToListAsync(cancellationToken);
			Guid[] expanded = retainedReceiptIds.Concat(pairs.SelectMany(pair => new[] { pair.ReceiptIdA, pair.ReceiptIdB })).Distinct().ToArray();
			if (expanded.Length == retainedReceiptIds.Length) { break; }
			retainedReceiptIds = expanded;
		}
		int retainedReceipts = await context.Receipts.IgnoreQueryFilters()
			.CountAsync(row => row.DeletedAt != null && retainedReceiptIds.Contains(row.Id), cancellationToken);
		int retainedItems = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.CountAsync(row => row.DeletedAt != null && retainedReceiptIds.Contains(row.ReceiptId), cancellationToken);
		IQueryable<Guid> retainedTransactions = context.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(row => retainedReceiptIds.Contains(row.ReceiptId)).Select(row => row.Id);

		int retainedRecords = retainedReceipts + retainedItems
			+ await context.Adjustments.IgnoreQueryFilters().CountAsync(row => row.DeletedAt != null && retainedReceiptIds.Contains(row.ReceiptId), cancellationToken)
			+ await context.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().CountAsync(row => row.DeletedAt != null && retainedReceiptIds.Contains(row.ReceiptId), cancellationToken)
			+ await context.YnabSyncRecords.IgnoreQueryFilters().CountAsync(row => row.DeletedAt != null && retainedTransactions.Contains(row.LocalTransactionId), cancellationToken)
			+ await context.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync(row => row.DeletedAt != null && (retainedReceiptIds.Contains(row.ReceiptIdA) || retainedReceiptIds.Contains(row.ReceiptIdB)), cancellationToken);

		// Delete in FK dependency order (children first)
		// Delete both soft-deleted sync records AND active sync records whose parent
		// Transaction is about to be purged. The YnabSyncRecords -> Transactions FK is
		// ClientCascade (DB-level NO ACTION), so an orphaned ACTIVE sync record pointing
		// at a soft-deleted transaction would block the Transactions delete below with an
		// FK violation — permanently breaking Empty Trash for every item. Purging it here
		// first keeps the operation FK-safe even for historical orphans. See RECEIPTS-755.
		await context.YnabSyncRecords
			.IgnoreQueryFilters()
			.Where(s => !retainedTransactions.Contains(s.LocalTransactionId) && (s.DeletedAt != null
				|| context.Transactions
					.IgnoreQueryFilters()
					.Any(t => t.Id == s.LocalTransactionId && t.DeletedAt != null)))
			.ExecuteDeleteAsync(cancellationToken);

		// Un-accepted duplicate pairs (RECEIPTS-834). These are soft-deleted so the un-accept shows up
		// in the audit log, but they are pure annotations — nothing surfaces them in the recycle bin,
		// so without this step the tombstones would accumulate forever. Purged before Receipts because
		// the pair rows are FK children of Receipts.
		await context.AcceptedDuplicatePairs
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null && !retainedReceiptIds.Contains(e.ReceiptIdA) && !retainedReceiptIds.Contains(e.ReceiptIdB))
			.ExecuteDeleteAsync(cancellationToken);

		await context.Adjustments
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null && !retainedReceiptIds.Contains(e.ReceiptId))
			.ExecuteDeleteAsync(cancellationToken);

		await context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null && !retainedReceiptIds.Contains(e.ReceiptId))
			.ExecuteDeleteAsync(cancellationToken);

		await context.Transactions
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null && !retainedReceiptIds.Contains(e.ReceiptId))
			.ExecuteDeleteAsync(cancellationToken);

		await context.Receipts
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null && !retainedReceiptIds.Contains(e.Id))
			.ExecuteDeleteAsync(cancellationToken);

		await context.ItemTemplates
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		// Envelopes and occasions are only soft-deleted while unreferenced; the reference guards keep
		// the Restrict FKs from failing the whole purge should a reference appear after deletion.
		await context.Envelopes
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null
				&& !context.ReceiptItems.IgnoreQueryFilters().Any(item => item.EnvelopeId == e.Id)
				&& !context.Receipts.IgnoreQueryFilters().Any(receipt => receipt.PaidFromEnvelopeId == e.Id)
				&& !context.ItemTypeEnvelopeDefaults.Any(row => row.EnvelopeId == e.Id))
			.ExecuteDeleteAsync(cancellationToken);

		await context.Occasions
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null
				&& !context.ReceiptItems.IgnoreQueryFilters().Any(item => item.OccasionId == e.Id)
				&& !context.Receipts.IgnoreQueryFilters().Any(receipt => receipt.OccasionId == e.Id))
			.ExecuteDeleteAsync(cancellationToken);

		// Delete both soft-deleted subcategories AND active subcategories
		// whose parent Category is about to be purged. The Subcategory → Category
		// FK is configured OnDelete(Cascade), so without this explicit step the
		// Category delete below would silently cascade-destroy any active
		// Subcategory rows pointing at a soft-deleted parent.
		await context.Subcategories
			.IgnoreQueryFilters()
			.Where(s => s.DeletedAt != null
				|| context.Categories
					.IgnoreQueryFilters()
					.Any(c => c.Id == s.CategoryId && c.DeletedAt != null))
			.ExecuteDeleteAsync(cancellationToken);

		await context.Categories
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await transaction.CommitAsync(cancellationToken);
		return new(retainedReceipts, retainedItems, "Receipts with classification history and their linked records stay in trash to preserve history. They can still be restored.", retainedRecords);
	}
}
