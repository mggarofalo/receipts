using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.MemoSync;

public class ResolveYnabMemoSyncCommandHandler(IYnabMemoSyncService memoSyncService) : IRequestHandler<ResolveYnabMemoSyncCommand, YnabMemoSyncResult>
{
	public async Task<YnabMemoSyncResult> Handle(ResolveYnabMemoSyncCommand request, CancellationToken cancellationToken)
	{
		return await memoSyncService.ResolveMemoSyncAsync(request.LocalTransactionId, request.YnabTransactionId, cancellationToken);
	}
}
