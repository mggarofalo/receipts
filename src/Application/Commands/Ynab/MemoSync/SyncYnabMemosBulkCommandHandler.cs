using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.MemoSync;

public class SyncYnabMemosBulkCommandHandler(IYnabMemoSyncService memoSyncService) : IRequestHandler<SyncYnabMemosBulkCommand, List<YnabMemoSyncResult>>
{
	public async Task<List<YnabMemoSyncResult>> Handle(SyncYnabMemosBulkCommand request, CancellationToken cancellationToken)
	{
		return await memoSyncService.SyncMemosBulkAsync(request.ReceiptIds, cancellationToken);
	}
}
