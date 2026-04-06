using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.MemoSync;

public class SyncYnabMemosCommandHandler(IYnabMemoSyncService memoSyncService) : IRequestHandler<SyncYnabMemosCommand, List<YnabMemoSyncResult>>
{
	public async Task<List<YnabMemoSyncResult>> Handle(SyncYnabMemosCommand request, CancellationToken cancellationToken)
	{
		return await memoSyncService.SyncMemosByReceiptAsync(request.ReceiptId, cancellationToken);
	}
}
