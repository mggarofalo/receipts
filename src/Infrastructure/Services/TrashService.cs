using Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class TrashService(ApplicationDbContext context) : ITrashService
{
	public async Task PurgeAllDeletedAsync(CancellationToken cancellationToken)
	{
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

		// Delete in FK dependency order (children first)
		// Delete both soft-deleted sync records AND active sync records whose parent
		// Transaction is about to be purged. The YnabSyncRecords -> Transactions FK is
		// ClientCascade (DB-level NO ACTION), so an orphaned ACTIVE sync record pointing
		// at a soft-deleted transaction would block the Transactions delete below with an
		// FK violation — permanently breaking Empty Trash for every item. Purging it here
		// first keeps the operation FK-safe even for historical orphans. See RECEIPTS-755.
		await context.YnabSyncRecords
			.IgnoreQueryFilters()
			.Where(s => s.DeletedAt != null
				|| context.Transactions
					.IgnoreQueryFilters()
					.Any(t => t.Id == s.LocalTransactionId && t.DeletedAt != null))
			.ExecuteDeleteAsync(cancellationToken);

		// Un-accepted duplicate pairs (RECEIPTS-834). These are soft-deleted so the un-accept shows up
		// in the audit log, but they are pure annotations — nothing surfaces them in the recycle bin,
		// so without this step the tombstones would accumulate forever. Purged before Receipts because
		// the pair rows are FK children of Receipts.
		await context.AcceptedDuplicatePairs
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.Adjustments
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.Transactions
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.Receipts
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.ItemTemplates
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
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
	}
}
