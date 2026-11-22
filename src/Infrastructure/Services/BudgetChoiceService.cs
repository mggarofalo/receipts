using Application.Interfaces.Services;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

/// <summary>
/// Explicit receipt and item budget choices. Writes take the receipt fact lock, the same lock every
/// classification write takes, so a concurrent item-type change cannot overwrite the derived envelope.
/// </summary>
public class BudgetChoiceService(IDbContextFactory<ApplicationDbContext> contextFactory) : IBudgetChoiceService
{
	public async Task<bool> SetReceiptChoicesAsync(Guid receiptId, Guid? paidFromEnvelopeId, Guid? occasionId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction? transaction = db.Database.IsRelational()
			? await db.Database.BeginTransactionAsync(cancellationToken) : null;
		// Gate before row locks, as every receipt writer does; saving item changes takes the gate too.
		await Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, cancellationToken);
		await ReceiptFactLock.AcquireAsync(db, [receiptId], cancellationToken);
		ReceiptEntity? receipt = await db.Receipts.SingleOrDefaultAsync(row => row.Id == receiptId, cancellationToken);
		if (receipt is null)
		{
			return false;
		}
		await ItemEnvelopeWriter.ApplyReceiptChoicesAsync(db, receipt, paidFromEnvelopeId, occasionId, cancellationToken);
		await db.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		return true;
	}

	public Task<bool> SetItemChoicesAsync(Guid itemId, Guid? envelopeId, Guid? occasionId, CancellationToken cancellationToken)
		=> WithLockedItemAsync(itemId, false, async (db, item) =>
		{
			await ItemEnvelopeWriter.ApplyItemChoicesAsync(db, item, envelopeId, occasionId, cancellationToken);
			return true;
		}, cancellationToken);

	public Task<ItemEnvelopeAutomaticResult> SetItemAutomaticAsync(Guid itemId, CancellationToken cancellationToken)
		=> WithLockedItemAsync(itemId, ItemEnvelopeAutomaticResult.NotFound, async (db, item) =>
			await ItemEnvelopeWriter.ApplyAutomaticAsync(db, item, cancellationToken)
				? ItemEnvelopeAutomaticResult.Applied : ItemEnvelopeAutomaticResult.Unresolvable, cancellationToken);

	private async Task<TResult> WithLockedItemAsync<TResult>(Guid itemId, TResult notFound,
		Func<ApplicationDbContext, ReceiptItemEntity, Task<TResult>> apply, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction? transaction = db.Database.IsRelational()
			? await db.Database.BeginTransactionAsync(cancellationToken) : null;
		Guid? receiptId = await db.ReceiptItems.AsNoTracking().IgnoreAutoIncludes().Where(row => row.Id == itemId)
			.Select(row => (Guid?)row.ReceiptId).SingleOrDefaultAsync(cancellationToken);
		if (receiptId is not Guid lockedReceipt)
		{
			return notFound;
		}
		await Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, cancellationToken);
		await ReceiptFactLock.AcquireAsync(db, [lockedReceipt], cancellationToken);
		ReceiptItemEntity? item = await db.ReceiptItems.IgnoreAutoIncludes()
			.SingleOrDefaultAsync(row => row.Id == itemId && row.ReceiptId == lockedReceipt, cancellationToken);
		if (item is null)
		{
			return notFound;
		}
		TResult result = await apply(db, item);
		await db.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		return result;
	}
}
