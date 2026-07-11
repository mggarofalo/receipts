using Application.Interfaces.Services;
using Application.Models;
using Domain.Aggregates;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace Application.Commands.Transaction.Update;

public class UpdateTransactionCommandHandler(ITransactionService transactionService)
	: IRequestHandler<UpdateTransactionCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateTransactionCommand request, CancellationToken cancellationToken)
	{
		Domain.Core.Transaction? existingTransaction = await transactionService.GetByIdAsync(request.Transactions[0].Id, cancellationToken);
		if (existingTransaction is null)
		{
			return false;
		}

		Guid receiptId = existingTransaction.ReceiptId;

		// Balance validation (RECEIPTS-764) runs inside the service's row-locked transaction so
		// the read-validate-write sequence is serialized per receipt at the DB level.
		await transactionService.UpdateWithBalanceValidationAsync(
			[.. request.Transactions],
			receiptId,
			state => Validate(state, request.Transactions),
			cancellationToken);

		return true;
	}

	private static void Validate(ReceiptBalanceState state, IReadOnlyList<Domain.Core.Transaction> incoming)
	{
		HashSet<Guid> receiptTransactionIds = [.. state.ExistingTransactions.Select(t => t.Id)];
		if (!incoming.All(t => receiptTransactionIds.Contains(t.Id)))
		{
			throw new InvalidOperationException("All transactions in the batch must belong to the same receipt.");
		}

		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = state.Receipt,
			Items = [.. state.Items],
			Adjustments = [.. state.Adjustments]
		};

		HashSet<Guid> updatedIds = [.. incoming.Select(t => t.Id)];
		decimal unchangedTotal = state.ExistingTransactions
			.Where(t => !updatedIds.Contains(t.Id))
			.Sum(t => t.Amount.Amount);
		decimal updatedTotal = incoming.Sum(t => t.Amount.Amount);
		decimal proposedTotal = unchangedTotal + updatedTotal;

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
