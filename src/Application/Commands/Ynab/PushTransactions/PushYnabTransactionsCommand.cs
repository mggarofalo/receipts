using Application.Interfaces;

namespace Application.Commands.Ynab.PushTransactions;

public record PushYnabTransactionsCommand(Guid ReceiptId) : ICommand<PushYnabTransactionsResult>;
