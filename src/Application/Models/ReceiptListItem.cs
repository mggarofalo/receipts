namespace Application.Models;

public sealed record ReceiptListItem(
	Guid Id,
	string Location,
	DateOnly Date,
	decimal TaxAmount,
	decimal ItemSubtotal,
	decimal AdjustmentTotal,
	decimal ExpectedTotal,
	decimal TransactionTotal,
	string BalanceState,
	int ItemCount,
	string CategorySummary,
	string PaymentSummary);
