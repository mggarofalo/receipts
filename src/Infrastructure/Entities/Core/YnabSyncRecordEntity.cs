using Common;
using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Core;

public class YnabSyncRecordEntity : ISoftDeletable
{
	public Guid Id { get; set; }
	public Guid LocalTransactionId { get; set; }
	public string? YnabTransactionId { get; set; }
	public string YnabBudgetId { get; set; } = string.Empty;
	public string? YnabAccountId { get; set; }
	public YnabSyncType SyncType { get; set; }
	public YnabSyncStatus SyncStatus { get; set; }
	public DateTimeOffset? SyncedAtUtc { get; set; }
	public string? LastError { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
	public virtual TransactionEntity? Transaction { get; set; }
	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }
	public Guid? CascadeDeletedByParentId { get; set; }
}
