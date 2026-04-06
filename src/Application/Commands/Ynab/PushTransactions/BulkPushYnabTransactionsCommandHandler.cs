using MediatR;

namespace Application.Commands.Ynab.PushTransactions;

public class BulkPushYnabTransactionsCommandHandler(IMediator mediator) : IRequestHandler<BulkPushYnabTransactionsCommand, BulkPushYnabTransactionsResult>
{
	public async Task<BulkPushYnabTransactionsResult> Handle(BulkPushYnabTransactionsCommand request, CancellationToken cancellationToken)
	{
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
