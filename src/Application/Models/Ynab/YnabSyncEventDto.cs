using Common;

namespace Application.Models.Ynab;

public record YnabSyncEventDto(
	Guid Id,
	DateTimeOffset OccurredAt,
	YnabSyncType EventType,
	YnabSyncStatus Outcome,
	Guid? LocalTransactionId,
	Guid? ReceiptId,
	string? YnabBudgetId,
	string? YnabTransactionId,
	string? ErrorMessage);
