namespace Application.Interfaces.Services;

public sealed record PurgeTrashResult(int RetainedReceipts, int RetainedItems, string Reason, int RetainedRecords = 0);

public interface ITrashService
{
	Task<PurgeTrashResult> PurgeAllDeletedAsync(CancellationToken cancellationToken);
}
