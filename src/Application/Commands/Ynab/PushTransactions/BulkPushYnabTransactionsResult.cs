namespace Application.Commands.Ynab.PushTransactions;

public record BulkPushYnabTransactionsResult(List<ReceiptPushResult> Results);

public record ReceiptPushResult(Guid ReceiptId, PushYnabTransactionsResult Result);
