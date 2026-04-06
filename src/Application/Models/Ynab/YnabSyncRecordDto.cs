using Common;

namespace Application.Models.Ynab;

public record YnabSyncRecordDto(
	Guid Id,
	Guid LocalTransactionId,
	string? YnabTransactionId,
	string YnabBudgetId,
	string? YnabAccountId,
	YnabSyncType SyncType,
	YnabSyncStatus SyncStatus,
	DateTimeOffset? SyncedAtUtc,
	string? LastError,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
