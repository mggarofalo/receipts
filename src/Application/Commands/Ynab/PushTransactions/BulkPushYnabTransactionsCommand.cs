using Application.Interfaces;

namespace Application.Commands.Ynab.PushTransactions;

public record BulkPushYnabTransactionsCommand(List<Guid> ReceiptIds) : ICommand<BulkPushYnabTransactionsResult>;
