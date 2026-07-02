using Common;

namespace Infrastructure.Entities.Core;

/// <summary>
/// Append-only log of YNAB integration attempts (pushes and connection validations).
/// One row is written per attempt so the /ynab status page can report health and recent
/// activity. Not soft-deletable and not audited (excluded in
/// <see cref="ApplicationDbContext.CollectAuditEntries"/>).
/// </summary>
public class YnabSyncEventEntity
{
	public Guid Id { get; set; }
	public string? UserId { get; set; }
	public DateTimeOffset OccurredAt { get; set; }
	public YnabSyncEventType EventType { get; set; }
	public Guid? ReceiptId { get; set; }
	public Guid? TransactionId { get; set; }
	public int? HttpStatus { get; set; }
	public bool Success { get; set; }
	public string? ErrorMessage { get; set; }
	public string? RequestId { get; set; }
}
