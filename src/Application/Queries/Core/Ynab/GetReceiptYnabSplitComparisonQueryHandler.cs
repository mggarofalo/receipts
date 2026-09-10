using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Common;
using Domain.Aggregates;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Application.Queries.Core.Ynab;

/// <summary>
/// Non-destructive read-only query that surfaces the expected YNAB split for a receipt
/// (via <see cref="IYnabSplitCalculator"/>) alongside the actual state currently stored
/// in YNAB, if the receipt has already been pushed.
///
/// Applies the same guards as <c>PushYnabTransactionsCommandHandler</c> but converts
/// failures into structured "unavailable" reasons instead of throwing, so the UI can
/// render a meaningful state regardless of configuration gaps.
/// </summary>
public class GetReceiptYnabSplitComparisonQueryHandler(
	IReceiptService receiptService,
	IReceiptItemService receiptItemService,
	IAdjustmentService adjustmentService,
	ITransactionService transactionService,
	IYnabCategoryMappingService categoryMappingService,
	IYnabAccountMappingService accountMappingService,
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabSyncRecordService syncRecordService,
	IYnabApiClient ynabApiClient,
	IYnabSplitCalculator splitCalculator,
	ILogger<GetReceiptYnabSplitComparisonQueryHandler> logger)
	: IRequestHandler<GetReceiptYnabSplitComparisonQuery, ReceiptYnabSplitComparisonResult>
{
	public async ValueTask<ReceiptYnabSplitComparisonResult> Handle(
		GetReceiptYnabSplitComparisonQuery request,
		CancellationToken cancellationToken)
	{
		Domain.Core.Receipt? receipt = await receiptService.GetByIdAsync(request.ReceiptId, cancellationToken);
		if (receipt is null)
		{
			return Unavailable("Receipt not found.");
		}

		if (receipt.TaxAmount.Currency != Currency.USD)
		{
			return Unavailable("Only USD receipts are supported for YNAB sync.");
		}

		PagedResult<Domain.Core.ReceiptItem> itemsResult = await receiptItemService.GetByReceiptIdAsync(
			request.ReceiptId, 0, 10000, new SortParams("Description", "asc"), cancellationToken);
		List<Domain.Core.ReceiptItem> items = itemsResult.Data.ToList();

		if (items.Count == 0)
		{
			return Unavailable("Receipt has no items.");
		}

		if (items.Any(i => i.TotalAmount.Currency != Currency.USD))
		{
			return Unavailable("Only USD receipts are supported for YNAB sync.");
		}

		PagedResult<Domain.Core.Adjustment> adjResult = await adjustmentService.GetByReceiptIdAsync(
			request.ReceiptId, 0, 10000, new SortParams("Type", "asc"), cancellationToken);
		List<Domain.Core.Adjustment> adjustments = adjResult.Data.ToList();

		List<TransactionAccount> transactionAccounts = await transactionService.GetTransactionAccountsByReceiptIdAsync(
			request.ReceiptId, cancellationToken);
		List<Domain.Core.Transaction> transactions = transactionAccounts.Select(ta => ta.Transaction).ToList();

		if (transactions.Count == 0)
		{
			return Unavailable("Receipt has no transactions.");
		}

		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return Unavailable("No YNAB budget selected.");
		}

		// Category mapping check — surface unmapped list as structured data
		List<string> distinctCategories = items.Select(i => i.Category).Distinct().ToList();
		List<YnabCategoryMappingDto> allMappings = await categoryMappingService.GetByBudgetIdAsync(budgetId, cancellationToken);
		Dictionary<string, string> categoryToYnabId = allMappings
			.ToDictionary(m => m.ReceiptsCategory, m => m.YnabCategoryId);

		List<string> unmapped = distinctCategories.Where(c => !categoryToYnabId.ContainsKey(c)).ToList();
		if (unmapped.Count > 0)
		{
			return new ReceiptYnabSplitComparisonResult(
				CanComputeExpected: false,
				ExpectedUnavailableReason: "Unmapped categories found.",
				UnmappedCategories: unmapped,
				TransactionComparisons: []);
		}

		List<YnabAccountMappingDto> accountMappingsList = await accountMappingService.GetByBudgetIdAsync(budgetId, cancellationToken);
		Dictionary<Guid, string> accountToYnabId = accountMappingsList
			.ToDictionary(m => m.ReceiptsAccountId, m => m.YnabAccountId);

		List<Guid> unmappedAccountIds = transactions
			.Select(t => t.AccountId)
			.Distinct()
			.Where(id => !accountToYnabId.ContainsKey(id))
			.ToList();

		if (unmappedAccountIds.Count > 0)
		{
			return Unavailable("Some transaction accounts are not mapped to YNAB accounts.");
		}

		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = receipt,
			Items = items,
			Adjustments = adjustments,
		};

		YnabSplitResult splitResult;
		try
		{
			splitResult = splitCalculator.ComputeWaterfallSplits(
				receiptWithItems, transactions, categoryToYnabId);
		}
		catch (InvalidOperationException ex)
		{
			logger.LogWarning(ex, "Split calculator failed for receipt {ReceiptId}", request.ReceiptId);
			return Unavailable(ex.Message);
		}

		// Load YNAB categories so we can render friendly names alongside IDs.
		Dictionary<string, string> categoryIdToName = new();
		try
		{
			List<YnabCategory> ynabCategories = await ynabApiClient.GetCategoriesAsync(budgetId, cancellationToken);
			foreach (YnabCategory cat in ynabCategories)
			{
				categoryIdToName[cat.Id] = cat.Name;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Non-fatal: we can still render expected milliunits, just with placeholder names.
			logger.LogWarning(ex, "Failed to fetch YNAB categories for budget {BudgetId}", budgetId);
		}

		// Look up account display names for each local transaction via the TransactionAccount join.
		Dictionary<Guid, string> transactionIdToAccountName = transactionAccounts
			.ToDictionary(ta => ta.Transaction.Id, ta => ta.Account.Name);

		List<TransactionSplitComparison> comparisons = [];
		foreach (YnabTransactionSplit txSplit in splitResult.TransactionSplits)
		{
			List<SplitLine> expected = txSplit.SubTransactions
				.Select(sub => new SplitLine(
					sub.YnabCategoryId,
					categoryIdToName.GetValueOrDefault(sub.YnabCategoryId, "(unknown)"),
					sub.Milliunits))
				.ToList();

			List<SplitLine>? actual = null;
			string? actualFetchError = null;
			bool? matches = null;

			YnabSyncRecordDto? syncRecord = await syncRecordService.GetByTransactionTypeAndBudgetAsync(
				txSplit.LocalTransactionId, YnabSyncType.TransactionPush, budgetId, cancellationToken);

			if (syncRecord is { SyncStatus: YnabSyncStatus.Synced, YnabTransactionId: not null })
			{
				try
				{
					YnabTransaction? ynabTx = await ynabApiClient.GetTransactionAsync(
						budgetId, syncRecord.YnabTransactionId, cancellationToken);

					if (ynabTx is not null)
					{
						if (ynabTx.SubTransactions is { Count: > 0 })
						{
							actual = ynabTx.SubTransactions
								.Select(st => new SplitLine(
									st.CategoryId ?? string.Empty,
									st.CategoryName ?? categoryIdToName.GetValueOrDefault(st.CategoryId ?? string.Empty, "(unknown)"),
									st.Amount))
								.ToList();
						}
						else if (ynabTx.CategoryId is not null)
						{
							actual =
							[
								new SplitLine(
									ynabTx.CategoryId,
									ynabTx.CategoryName ?? categoryIdToName.GetValueOrDefault(ynabTx.CategoryId, "(unknown)"),
									ynabTx.Amount),
							];
						}
						else
						{
							// Pushed transaction with neither subtransactions nor a category —
							// surface this as a fetch-level inconsistency rather than an empty actual.
							actualFetchError = "YNAB transaction has no categories or subtransactions.";
						}

						if (actual is not null)
						{
							matches = SplitsMatch(expected, actual);
						}
					}
					else
					{
						actualFetchError = "YNAB transaction not found.";
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex, "Failed to fetch YNAB transaction {YnabTransactionId}", syncRecord.YnabTransactionId);
					actualFetchError = ex.Message;
				}
			}

			string accountName = transactionIdToAccountName.GetValueOrDefault(
				txSplit.LocalTransactionId, "(unknown)");

			comparisons.Add(new TransactionSplitComparison(
				LocalTransactionId: txSplit.LocalTransactionId,
				AccountName: accountName,
				TotalMilliunits: txSplit.TotalMilliunits,
				Expected: expected,
				Actual: actual,
				ActualFetchError: actualFetchError,
				Matches: matches));
		}

		return new ReceiptYnabSplitComparisonResult(
			CanComputeExpected: true,
			ExpectedUnavailableReason: null,
			UnmappedCategories: [],
			TransactionComparisons: comparisons);
	}

	private static ReceiptYnabSplitComparisonResult Unavailable(string reason) =>
		new(
			CanComputeExpected: false,
			ExpectedUnavailableReason: reason,
			UnmappedCategories: [],
			TransactionComparisons: []);

	/// <summary>
	/// Compares two split lists as unordered multisets of (categoryId, milliunits) pairs.
	/// Returns true only when both lists contain identical entries regardless of order.
	/// </summary>
	private static bool SplitsMatch(List<SplitLine> expected, List<SplitLine> actual)
	{
		if (expected.Count != actual.Count)
		{
			return false;
		}

		Dictionary<(string CategoryId, long Milliunits), int> counts = new();
		foreach (SplitLine line in expected)
		{
			(string, long) key = (line.YnabCategoryId, line.Milliunits);
			counts[key] = counts.GetValueOrDefault(key) + 1;
		}

		foreach (SplitLine line in actual)
		{
			(string, long) key = (line.YnabCategoryId, line.Milliunits);
			if (!counts.TryGetValue(key, out int count) || count == 0)
			{
				return false;
			}
			counts[key] = count - 1;
		}

		return counts.Values.All(c => c == 0);
	}
}
