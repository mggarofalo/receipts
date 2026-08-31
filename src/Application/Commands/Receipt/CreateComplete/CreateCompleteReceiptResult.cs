namespace Application.Commands.Receipt.CreateComplete;

public record CreateCompleteReceiptResult(
	Domain.Core.Receipt Receipt,
	List<Domain.Core.Transaction> Transactions,
	List<Domain.Core.ReceiptItem> Items,
	List<Domain.Core.Adjustment> Adjustments);
