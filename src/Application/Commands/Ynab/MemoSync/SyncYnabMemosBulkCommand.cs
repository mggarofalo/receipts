using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.MemoSync;

public record SyncYnabMemosBulkCommand(List<Guid> ReceiptIds) : ICommand<List<YnabMemoSyncResult>>;
