namespace Application.Models.Ynab;

public record YnabSyncEventDto(
	Guid Id,
	DateTimeOffset OccurredAt,
	string EventType,
	Guid? ReceiptId,
	Guid? TransactionId,
	int? HttpStatus,
	bool Success,
	string? ErrorMessage,
	string? RequestId);
