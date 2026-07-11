using Application.Interfaces.Services;
using Domain.Aggregates;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace Application.Commands.Transaction.Update;

public class UpdateTransactionCommandHandler(
	ITransactionService transactionService,
	IReceiptService receiptService,
	IReceiptItemService receiptItemService,
	IAdjustmentService adjustmentService) : IRequestHandler<UpdateTransactionCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateTransactionCommand request, CancellationToken cancellationToken)
	{
		Domain.Core.Transaction? existingTransaction = await transactionService.GetByIdAsync(request.Transactions[0].Id, cancellationToken);
		if (existingTransaction is null)
		{
			return false;
		}

		Guid receiptId = existingTransaction.ReceiptId;

		Task<Domain.Core.Receipt?> receiptTask = receiptService.GetByIdAsync(receiptId, cancellationToken);
		Task<Models.PagedResult<Domain.Core.ReceiptItem>> itemsTask = receiptItemService.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, Models.SortParams.Default, cancellationToken);
		Task<Models.PagedResult<Domain.Core.Adjustment>> adjustmentsTask = adjustmentService.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, Models.SortParams.Default, cancellationToken);
		Task<Models.PagedResult<Domain.Core.Transaction>> existingTransactionsTask = transactionService.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, Models.SortParams.Default, cancellationToken);

		await Task.WhenAll(receiptTask, itemsTask, adjustmentsTask, existingTransactionsTask);

		Domain.Core.Receipt receipt = receiptTask.Result
			?? throw new InvalidOperationException("Receipt not found");

		HashSet<Guid> receiptTransactionIds = [.. existingTransactionsTask.Result.Data.Select(t => t.Id)];
		if (!request.Transactions.All(t => receiptTransactionIds.Contains(t.Id)))
		{
			throw new InvalidOperationException("All transactions in the batch must belong to the same receipt.");
		}

		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = receipt,
			Items = itemsTask.Result.Data,
			Adjustments = adjustmentsTask.Result.Data
		};

		HashSet<Guid> updatedIds = [.. request.Transactions.Select(t => t.Id)];
		decimal unchangedTotal = existingTransactionsTask.Result.Data
			.Where(t => !updatedIds.Contains(t.Id))
			.Sum(t => t.Amount.Amount);
		decimal updatedTotal = request.Transactions.Sum(t => t.Amount.Amount);
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

		await transactionService.UpdateAsync([.. request.Transactions], receiptId, cancellationToken);
		return true;
	}
}
