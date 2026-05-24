using Common;

namespace Infrastructure.Entities.Core;

// Append-only activity log of YNAB sync attempts. One row per state transition
// observed by YnabSyncRecordService.UpdateStatusAsync. Complements
// YnabSyncRecordEntity (which holds current state; retries upsert that row);
// this table preserves retry history for the /ynab status page.
public class YnabSyncEventEntity
{
	public Guid Id { get; set; }
	public DateTimeOffset OccurredAt { get; set; }
	public YnabSyncType EventType { get; set; }
	public YnabSyncStatus Outcome { get; set; }
	public Guid? LocalTransactionId { get; set; }
	public Guid? ReceiptId { get; set; }
	public string? YnabBudgetId { get; set; }
	public string? YnabTransactionId { get; set; }
	public string? ErrorMessage { get; set; }
}
