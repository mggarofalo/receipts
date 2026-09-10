using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Commands.Ynab.PushTransactions;

public class BulkPushYnabTransactionsCommandHandler(
	IMediator mediator,
	IYnabRateLimitTracker rateLimitTracker,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<BulkPushYnabTransactionsCommand, BulkPushYnabTransactionsResult>
{
	// Conservative estimate: each receipt push uses ~2 YNAB API calls
	private const int EstimatedRequestsPerReceipt = 2;

	public async ValueTask<BulkPushYnabTransactionsResult> Handle(BulkPushYnabTransactionsCommand request, CancellationToken cancellationToken)
	{
		// A bulk submission is one user operation. Capture its destination once so a
		// concurrent settings change cannot split receipts across YNAB budgets.
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return new BulkPushYnabTransactionsResult(
				[.. request.ReceiptIds.Select(id => new ReceiptPushResult(
					id,
					PushYnabTransactionsResult.Failure("No YNAB budget selected.")))]);
		}

		int estimatedRequests = request.ReceiptIds.Count * EstimatedRequestsPerReceipt;
		if (!rateLimitTracker.CanMakeRequests(estimatedRequests))
		{
			YnabRateLimitStatus status = rateLimitTracker.GetStatus();
			List<ReceiptPushResult> failedResults = request.ReceiptIds
				.Select(id => new ReceiptPushResult(id, PushYnabTransactionsResult.Failure(
					$"YNAB API rate limit would be exceeded. {status.RemainingRequests}/{status.MaxRequests} requests remaining.{(status.WindowResetAt.HasValue ? $" Try again after {status.WindowResetAt.Value:HH:mm:ss} UTC." : "")}")))
				.ToList();
			return new BulkPushYnabTransactionsResult(failedResults);
		}

		List<ReceiptPushResult> results = [];

		foreach (Guid receiptId in request.ReceiptIds)
		{
			try
			{
				PushYnabTransactionsResult result = await mediator.Send(
					new PushYnabTransactionsCommand(receiptId, budgetId), cancellationToken);
				results.Add(new ReceiptPushResult(receiptId, result));
			}
			catch (Exception ex)
			{
				PushYnabTransactionsResult failureResult = new(false, [], Error: $"Unexpected error: {ex.Message}");
				results.Add(new ReceiptPushResult(receiptId, failureResult));
			}
		}

		return new BulkPushYnabTransactionsResult(results);
	}
}
