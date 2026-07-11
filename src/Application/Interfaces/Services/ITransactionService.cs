using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface ITransactionService : ISoftDeletableService<Transaction>
{
	Task<PagedResult<Transaction>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<List<Domain.Aggregates.TransactionAccount>> GetTransactionAccountsByReceiptIdAsync(Guid receiptId, CancellationToken cancellationToken);
	Task<List<Transaction>> CreateAsync(List<Transaction> models, Guid receiptId, CancellationToken cancellationToken);
	Task UpdateAsync(List<Transaction> models, Guid receiptId, CancellationToken cancellationToken);

	/// <summary>
	/// Creates the supplied transactions under <paramref name="receiptId"/> with the receipt
	/// balance invariant serialized at the database level (RECEIPTS-764). The receipt row is
	/// locked for the duration of a single transaction, the receipt's children are re-read
	/// inside that lock, <paramref name="validate"/> is invoked against that fresh snapshot,
	/// and only then are the new transactions inserted and the transaction committed. Two
	/// concurrent calls for the same receipt therefore serialize on the receipt row, so the
	/// second observes the first's write and can correctly reject an over-allocation.
	/// Throws <see cref="KeyNotFoundException"/> if the receipt does not exist or is
	/// soft-deleted (RECEIPTS-763).
	/// </summary>
	Task<List<Transaction>> CreateWithBalanceValidationAsync(List<Transaction> models, Guid receiptId, Action<ReceiptBalanceState> validate, CancellationToken cancellationToken);

	/// <summary>
	/// Updates the supplied transactions under <paramref name="receiptId"/> with the same
	/// per-receipt, database-level serialization as
	/// <see cref="CreateWithBalanceValidationAsync"/>. See RECEIPTS-764.
	/// </summary>
	Task UpdateWithBalanceValidationAsync(List<Transaction> models, Guid receiptId, Action<ReceiptBalanceState> validate, CancellationToken cancellationToken);
}