using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.MemoSync;

public record SyncYnabMemosCommand(Guid ReceiptId) : ICommand<List<YnabMemoSyncResult>>;
