using Application.Interfaces.Services;
using Application.Models;
using Domain.Aggregates;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace Application.Commands.Transaction.Create;

public class CreateTransactionCommandHandler(ITransactionService transactionService)
	: IRequestHandler<CreateTransactionCommand, List<Domain.Core.Transaction>>
{
	public async ValueTask<List<Domain.Core.Transaction>> Handle(CreateTransactionCommand request, CancellationToken cancellationToken)
	{
		// The receipt existence / soft-delete guard (RECEIPTS-763) and the balance-equation
		// validation (RECEIPTS-764) both run inside a single row-locked transaction owned by the
		// service, so the read-validate-write sequence is serialized per receipt at the DB level.
		return await transactionService.CreateWithBalanceValidationAsync(
			[.. request.Transactions],
			request.ReceiptId,
			state => Validate(state, request.Transactions),
			cancellationToken);
	}

	private static void Validate(ReceiptBalanceState state, IReadOnlyList<Domain.Core.Transaction> incoming)
	{
		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = state.Receipt,
			Items = [.. state.Items],
			Adjustments = [.. state.Adjustments]
		};

		decimal existingTotal = state.ExistingTransactions.Sum(t => t.Amount.Amount);
		decimal newTotal = incoming.Sum(t => t.Amount.Amount);
		decimal proposedTotal = existingTotal + newTotal;

		if (Math.Abs(proposedTotal - receiptWithItems.ExpectedTotal.Amount) > 0.01m)
		{
			throw new ValidationException(
			[
				new ValidationFailure("Transactions",
					string.Format(Trip.BalanceEquationViolation,
						receiptWithItems.ExpectedTotal.Amount, proposedTotal))
			]);
		}
	}
}
