using Application.Interfaces.Services;
using Domain.Aggregates;
using Domain.Core;
using Infrastructure.Utilities;

namespace Infrastructure.Services;

/// <summary>
/// Pure calculation engine that computes YNAB split transaction amounts from receipt data.
/// All intermediate arithmetic uses raw decimal to avoid Money operator rounding.
/// </summary>
public class YnabSplitCalculator : IYnabSplitCalculator
{
	/// <summary>
	/// Computes per-category allocations from receipt data.
	/// Groups items by YNAB category, then proportionally allocates tax and adjustments.
	/// </summary>
	internal static List<YnabCategoryAllocation> ComputeCategoryAllocations(
		ReceiptWithItems receiptWithItems,
		Dictionary<string, string> categoryToYnabCategoryId)
	{
		List<ReceiptItem> items = receiptWithItems.Items;
		Receipt receipt = receiptWithItems.Receipt;

		// Group items by YNAB category ID and compute pre-tax sum per group
		Dictionary<string, decimal> categoryPreTaxSums = new();
		foreach (ReceiptItem item in items)
		{
			string ynabCategoryId = categoryToYnabCategoryId[item.Category];
			decimal amount = item.TotalAmount.Amount;
			if (categoryPreTaxSums.TryGetValue(ynabCategoryId, out decimal existing))
			{
				categoryPreTaxSums[ynabCategoryId] = existing + amount;
			}
			else
			{
				categoryPreTaxSums[ynabCategoryId] = amount;
			}
		}

		// Receipt-level totals (raw decimal, no Money operators)
		decimal receiptPreTaxTotal = items.Sum(i => i.TotalAmount.Amount);
		decimal receiptTaxAmount = receipt.TaxAmount.Amount;
		decimal receiptAdjustmentTotal = receiptWithItems.Adjustments.Sum(a => a.Amount.Amount);

		List<YnabCategoryAllocation> allocations = [];

		foreach (KeyValuePair<string, decimal> kvp in categoryPreTaxSums)
		{
			string ynabCategoryId = kvp.Key;
			decimal categoryPreTaxSum = kvp.Value;

			// Proportional allocation using raw decimal arithmetic
			decimal weight = receiptPreTaxTotal == 0m ? 0m : categoryPreTaxSum / receiptPreTaxTotal;
			decimal categoryTax = weight * receiptTaxAmount;
			decimal categoryAdj = weight * receiptAdjustmentTotal;
			decimal categoryTotal = categoryPreTaxSum + categoryTax + categoryAdj;
			long milliunits = YnabConvert.ToMilliunits(categoryTotal);

			allocations.Add(new YnabCategoryAllocation(
				ynabCategoryId,
				categoryPreTaxSum,
				categoryTax,
				categoryAdj,
				categoryTotal,
				milliunits));
		}

		// Sort by absolute amount descending for deterministic waterfall ordering
		allocations.Sort((a, b) => Math.Abs(b.PreTaxAmount).CompareTo(Math.Abs(a.PreTaxAmount)));

		return allocations;
	}

	/// <summary>
	/// Applies the largest-remainder method to correct milliunit rounding within a single transaction.
	/// Ensures the sum of sub-transaction milliunits exactly equals the transaction total.
	/// </summary>
	internal static List<YnabSubTransactionSplit> ApplyLargestRemainderCorrection(
		long transactionTotalMilliunits,
		List<YnabSubTransactionSplit> subTransactions)
	{
		if (subTransactions.Count == 0)
		{
			return subTransactions;
		}

		long sumOfSubs = subTransactions.Sum(st => st.Milliunits);
		long remainder = transactionTotalMilliunits - sumOfSubs;

		// Safety: remainder should not exceed the number of sub-transactions
		if (Math.Abs(remainder) > subTransactions.Count)
		{
			throw new InvalidOperationException(
				$"Rounding remainder ({remainder}) exceeds sub-transaction count ({subTransactions.Count}). " +
				$"Transaction total: {transactionTotalMilliunits}, sum of subs: {sumOfSubs}.");
		}

		if (remainder == 0)
		{
			return subTransactions;
		}

		// Sort by absolute milliunit value descending — distribute to largest first
		List<YnabSubTransactionSplit> sorted = subTransactions
			.Select((st, idx) => (st, idx))
			.OrderByDescending(x => Math.Abs(x.st.Milliunits))
			.ThenBy(x => x.idx)
			.Select(x => x.st)
			.ToList();

		long adjustment = remainder > 0 ? 1 : -1;
		List<YnabSubTransactionSplit> corrected = new(sorted);

		for (int i = 0; i < Math.Abs(remainder); i++)
		{
			YnabSubTransactionSplit original = corrected[i];
			corrected[i] = original with { Milliunits = original.Milliunits + adjustment };
		}

		return corrected;
	}

	/// <inheritdoc />
	public YnabSplitResult ComputeWaterfallSplits(
		ReceiptWithItems receiptWithItems,
		List<Transaction> transactions,
		Dictionary<string, string> categoryToYnabCategoryId)
	{
		// 1. Compute per-category allocations for the entire receipt
		List<YnabCategoryAllocation> allocations = ComputeCategoryAllocations(receiptWithItems, categoryToYnabCategoryId);

		// 2. Order transactions by absolute amount descending
		List<Transaction> orderedTransactions = transactions
			.OrderByDescending(t => Math.Abs(t.Amount.Amount))
			.ToList();

		// 3. Waterfall: fill each transaction sequentially
		List<YnabTransactionSplit> transactionSplits = [];

		// Build a mutable list of remaining milliunits per category.
		// Negate: local positive (outflow) → YNAB negative (outflow), matching txMilliunits sign.
		List<(string YnabCategoryId, long RemainingMilliunits)> remaining = allocations
			.Select(a => (a.YnabCategoryId, RemainingMilliunits: -a.Milliunits))
			.ToList();

		for (int txIdx = 0; txIdx < orderedTransactions.Count; txIdx++)
		{
			Transaction tx = orderedTransactions[txIdx];
			// Sign convention: negate local amount for YNAB (positive local = negative YNAB outflow)
			long txMilliunits = -YnabConvert.ToMilliunits(tx.Amount.Amount);

			List<YnabSubTransactionSplit> subs = [];
			long budgetRemaining = txMilliunits;
			bool isLastTransaction = txIdx == orderedTransactions.Count - 1;

			for (int catIdx = 0; catIdx < remaining.Count; catIdx++)
			{
				(string ynabCategoryId, long catMilliunits) = remaining[catIdx];

				if (catMilliunits == 0)
				{
					continue;
				}

				if (isLastTransaction)
				{
					// Last transaction gets all remaining
					subs.Add(new YnabSubTransactionSplit(ynabCategoryId, catMilliunits));
					remaining[catIdx] = (ynabCategoryId, 0);
				}
				else
				{
					// Determine how much of this category fits in this transaction
					// Both amounts are negative (outflows), so we work with the same sign
					long absRemaining = Math.Abs(budgetRemaining);
					long absCat = Math.Abs(catMilliunits);

					if (absCat <= absRemaining)
					{
						// Whole category fits
						subs.Add(new YnabSubTransactionSplit(ynabCategoryId, catMilliunits));
						budgetRemaining -= catMilliunits;
						remaining[catIdx] = (ynabCategoryId, 0);
					}
					else
					{
						// Category straddles boundary — split it
						long portion = budgetRemaining;
						subs.Add(new YnabSubTransactionSplit(ynabCategoryId, portion));
						long overflow = catMilliunits - portion;
						remaining[catIdx] = (ynabCategoryId, overflow);
						budgetRemaining = 0;
						break;
					}
				}
			}

			// Apply largest-remainder correction for this transaction independently
			if (subs.Count > 0)
			{
				subs = ApplyLargestRemainderCorrection(txMilliunits, subs);
			}

			transactionSplits.Add(new YnabTransactionSplit(tx.Id, txMilliunits, subs));
		}

		return new YnabSplitResult(transactionSplits);
	}
}
