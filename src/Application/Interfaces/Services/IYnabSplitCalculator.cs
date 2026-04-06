using Domain.Aggregates;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IYnabSplitCalculator
{
	/// <summary>
	/// Computes the full split result for a receipt with potentially multiple transactions.
	/// Implements the waterfall algorithm: fills transactions sequentially, splitting categories
	/// across transaction boundaries when needed.
	/// </summary>
	YnabSplitResult ComputeWaterfallSplits(
		ReceiptWithItems receiptWithItems,
		List<Transaction> transactions,
		Dictionary<string, string> categoryToYnabCategoryId);
}

public record YnabCategoryAllocation(string YnabCategoryId, decimal PreTaxAmount, decimal TaxAmount, decimal AdjustmentAmount, decimal Total, long Milliunits);

public record YnabTransactionSplit(
	Guid LocalTransactionId,
	long TotalMilliunits,
	List<YnabSubTransactionSplit> SubTransactions);

public record YnabSubTransactionSplit(string YnabCategoryId, long Milliunits);

public record YnabSplitResult(List<YnabTransactionSplit> TransactionSplits);
