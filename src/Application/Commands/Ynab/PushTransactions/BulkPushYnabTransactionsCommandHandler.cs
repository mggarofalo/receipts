using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.PushTransactions;

public class BulkPushYnabTransactionsCommandHandler(IMediator mediator, IYnabRateLimitTracker rateLimitTracker) : IRequestHandler<BulkPushYnabTransactionsCommand, BulkPushYnabTransactionsResult>
{
	// Conservative estimate: each receipt push uses ~2 YNAB API calls
	private const int EstimatedRequestsPerReceipt = 2;

	public async Task<BulkPushYnabTransactionsResult> Handle(BulkPushYnabTransactionsCommand request, CancellationToken cancellationToken)
	{
		int estimatedRequests = request.ReceiptIds.Count * EstimatedRequestsPerReceipt;
		if (!rateLimitTracker.CanMakeRequests(estimatedRequests))
		{
			YnabRateLimitStatus status = rateLimitTracker.GetStatus();
			List<ReceiptPushResult> failedResults = request.ReceiptIds
				.Select(id => new ReceiptPushResult(id, PushYnabTransactionsResult.Failure(
					$"YNAB API rate limit would be exceeded. {status.RemainingRequests}/{status.MaxRequests} requests remaining. Try again after {status.WindowResetAt:HH:mm:ss} UTC.")))
				.ToList();
			return new BulkPushYnabTransactionsResult(failedResults);
		}

		List<ReceiptPushResult> results = [];

		foreach (Guid receiptId in request.ReceiptIds)
		{
			PushYnabTransactionsResult result = await mediator.Send(
				new PushYnabTransactionsCommand(receiptId), cancellationToken);
			results.Add(new ReceiptPushResult(receiptId, result));
		}

		return new BulkPushYnabTransactionsResult(results);
	}
}
