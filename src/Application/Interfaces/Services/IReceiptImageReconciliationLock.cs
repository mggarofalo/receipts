namespace Application.Interfaces.Services;

/// <summary>
/// Excludes receipt image publication from reconciliation operations that can delete
/// unreferenced files. The lease must be held through the durable path update.
/// </summary>
public interface IReceiptImageReconciliationLock
{
	Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}
