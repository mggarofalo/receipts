using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.MemoSync;

public class SyncYnabMemosBulkCommandHandler(IYnabMemoSyncService memoSyncService, IYnabRateLimitTracker rateLimitTracker) : IRequestHandler<SyncYnabMemosBulkCommand, List<YnabMemoSyncResult>>
{
	// Each receipt memo sync may involve: 1 GetTransactionsByDate + N UpdateTransactionMemo calls
	// Conservative estimate: ~3 API calls per receipt
	private const int EstimatedRequestsPerReceipt = 3;

	public async Task<List<YnabMemoSyncResult>> Handle(SyncYnabMemosBulkCommand request, CancellationToken cancellationToken)
	{
		int estimatedRequests = request.ReceiptIds.Count * EstimatedRequestsPerReceipt;
		if (!rateLimitTracker.CanMakeRequests(estimatedRequests))
		{
			YnabRateLimitStatus status = rateLimitTracker.GetStatus();
			string error = $"YNAB API rate limit would be exceeded. {status.RemainingRequests}/{status.MaxRequests} requests remaining. Try again after {status.WindowResetAt:HH:mm:ss} UTC.";
			return request.ReceiptIds
				.Select(id => new YnabMemoSyncResult(Guid.Empty, id, YnabMemoSyncOutcome.Failed, null, error, null))
				.ToList();
		}

		return await memoSyncService.SyncMemosBulkAsync(request.ReceiptIds, cancellationToken);
	}
}
