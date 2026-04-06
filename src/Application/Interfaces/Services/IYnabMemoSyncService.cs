using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabMemoSyncService
{
	/// <summary>
	/// Sync YNAB memos for all transactions belonging to a single receipt.
	/// </summary>
	Task<List<YnabMemoSyncResult>> SyncMemosByReceiptAsync(Guid receiptId, CancellationToken cancellationToken);

	/// <summary>
	/// Sync YNAB memos for all transactions across multiple receipts.
	/// </summary>
	Task<List<YnabMemoSyncResult>> SyncMemosBulkAsync(List<Guid> receiptIds, CancellationToken cancellationToken);

	/// <summary>
	/// Resolve an ambiguous match by directly linking a local transaction to a chosen YNAB transaction.
	/// </summary>
	Task<YnabMemoSyncResult> ResolveMemoSyncAsync(Guid localTransactionId, string ynabTransactionId, CancellationToken cancellationToken);
}
