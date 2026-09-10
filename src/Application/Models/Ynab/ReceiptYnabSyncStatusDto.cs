namespace Application.Models.Ynab;

public enum ReceiptSyncStatusValue
{
	NotSynced,
	Pending,
	Synced,
	Failed,
	Unknown
}

public record ReceiptYnabSyncStatusDto(Guid ReceiptId, ReceiptSyncStatusValue SyncStatus);
