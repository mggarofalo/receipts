using Application.Interfaces.Services;
using Domain.Aggregates;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace Application.Commands.Receipt.CreateComplete;

public class CreateCompleteReceiptCommandHandler(
	ICompleteReceiptService completeReceiptService) : IRequestHandler<CreateCompleteReceiptCommand, CreateCompleteReceiptResult>
{
	public async ValueTask<CreateCompleteReceiptResult> Handle(CreateCompleteReceiptCommand request, CancellationToken cancellationToken)
	{
		if (request.Transactions.Count > 0)
		{
			ReceiptWithItems receiptWithItems = new()
			{
				Receipt = request.Receipt,
				Items = [.. request.Items],
				Adjustments = [.. request.Adjustments]
			};

			decimal expectedTotal = receiptWithItems.ExpectedTotal.Amount;
			decimal transactionTotal = request.Transactions.Sum(t => t.Amount.Amount);

			if (Math.Abs(expectedTotal - transactionTotal) > 0.01m)
			{
				throw new ValidationException(
				[
					new ValidationFailure("Transactions",
						string.Format(Trip.BalanceEquationViolation,
							expectedTotal, transactionTotal))
				]);
			}
		}

		return await completeReceiptService.CreateAsync(
			request.Receipt,
			[.. request.Transactions],
			[.. request.Items],
			[.. request.Adjustments],
			cancellationToken);
	}
}
