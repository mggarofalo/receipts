using Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class TrashService(ApplicationDbContext context) : ITrashService
{
	public async Task PurgeAllDeletedAsync(CancellationToken cancellationToken)
	{
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

		// Delete in FK dependency order (children first)
		await context.Adjustments
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(e => e.DeletedAt != null)
			.ExecuteDeleteAsync(cancellationToken);

		await context.YnabSyncRecords
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

		await transaction.CommitAsync(cancellationToken);
	}
}
