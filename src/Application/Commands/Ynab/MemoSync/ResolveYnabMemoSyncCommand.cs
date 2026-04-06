using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.MemoSync;

public record ResolveYnabMemoSyncCommand(Guid LocalTransactionId, string YnabTransactionId) : ICommand<YnabMemoSyncResult>;
