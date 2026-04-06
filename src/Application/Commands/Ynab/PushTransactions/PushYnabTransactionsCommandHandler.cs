using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Utilities;
using Common;
using Domain.Aggregates;
using MediatR;

namespace Application.Commands.Ynab.PushTransactions;

public class PushYnabTransactionsCommandHandler(
	IReceiptService receiptService,
	IReceiptItemService receiptItemService,
	IAdjustmentService adjustmentService,
	ITransactionService transactionService,
	IYnabCategoryMappingService categoryMappingService,
	IYnabAccountMappingService accountMappingService,
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabSyncRecordService syncRecordService,
	IYnabApiClient ynabApiClient,
	IYnabSplitCalculator splitCalculator) : IRequestHandler<PushYnabTransactionsCommand, PushYnabTransactionsResult>
{
	public async Task<PushYnabTransactionsResult> Handle(PushYnabTransactionsCommand request, CancellationToken cancellationToken)
	{
		// 1. Load the receipt and related data
		Domain.Core.Receipt? receipt = await receiptService.GetByIdAsync(request.ReceiptId, cancellationToken);
		if (receipt is null)
		{
			return new PushYnabTransactionsResult(false, [], Error: "Receipt not found.");
		}

		// Currency guard: USD only (V1)
		if (receipt.TaxAmount.Currency != Currency.USD)
		{
			return new PushYnabTransactionsResult(false, [], Error: "Only USD receipts are supported for YNAB sync.");
		}

		PagedResult<Domain.Core.ReceiptItem> itemsResult = await receiptItemService.GetByReceiptIdAsync(
			request.ReceiptId, 0, 10000, new SortParams("Description", "asc"), cancellationToken);
		List<Domain.Core.ReceiptItem> items = itemsResult.Data.ToList();

		if (items.Count == 0)
		{
			return new PushYnabTransactionsResult(false, [], Error: "Receipt has no items.");
		}

		// Currency guard on items
		if (items.Any(i => i.TotalAmount.Currency != Currency.USD))
		{
			return new PushYnabTransactionsResult(false, [], Error: "Only USD receipts are supported for YNAB sync.");
		}

		PagedResult<Domain.Core.Adjustment> adjResult = await adjustmentService.GetByReceiptIdAsync(
			request.ReceiptId, 0, 10000, new SortParams("Type", "asc"), cancellationToken);
		List<Domain.Core.Adjustment> adjustments = adjResult.Data.ToList();

		List<TransactionAccount> transactionAccounts = await transactionService.GetTransactionAccountsByReceiptIdAsync(
			request.ReceiptId, cancellationToken);
		List<Domain.Core.Transaction> transactions = transactionAccounts.Select(ta => ta.Transaction).ToList();

		if (transactions.Count == 0)
		{
			return new PushYnabTransactionsResult(false, [], Error: "Receipt has no transactions.");
		}

		// 2. Check all categories are mapped (fail-fast)
		List<string> distinctCategories = items.Select(i => i.Category).Distinct().ToList();
		List<YnabCategoryMappingDto> allMappings = await categoryMappingService.GetAllAsync(cancellationToken);
		Dictionary<string, string> categoryToYnabId = allMappings
			.ToDictionary(m => m.ReceiptsCategory, m => m.YnabCategoryId);

		List<string> unmapped = distinctCategories.Where(c => !categoryToYnabId.ContainsKey(c)).ToList();
		if (unmapped.Count > 0)
		{
			return new PushYnabTransactionsResult(false, [], UnmappedCategories: unmapped, Error: "Unmapped categories found.");
		}

		// 3. Get selected budget
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return new PushYnabTransactionsResult(false, [], Error: "No YNAB budget selected.");
		}

		// 4. Get account mappings for the transactions
		List<YnabAccountMappingDto> accountMappingsList = await accountMappingService.GetAllAsync(cancellationToken);
		Dictionary<Guid, string> accountToYnabId = accountMappingsList
			.ToDictionary(m => m.ReceiptsAccountId, m => m.YnabAccountId);

		// Check all transaction accounts have YNAB mappings
		List<Guid> unmappedAccountIds = transactions
			.Select(t => t.AccountId)
			.Distinct()
			.Where(id => !accountToYnabId.ContainsKey(id))
			.ToList();

		if (unmappedAccountIds.Count > 0)
		{
			return new PushYnabTransactionsResult(false, [], Error: "Some transaction accounts are not mapped to YNAB accounts.");
		}

		// 5. Check no transaction is already synced
		foreach (Domain.Core.Transaction tx in transactions)
		{
			YnabSyncRecordDto? existingSync = await syncRecordService.GetByTransactionAndTypeAsync(
				tx.Id, YnabSyncType.TransactionPush, cancellationToken);
			if (existingSync is not null && existingSync.SyncStatus == YnabSyncStatus.Synced)
			{
				return new PushYnabTransactionsResult(false, [],
					Error: $"Transaction {tx.Id} has already been synced to YNAB.");
			}
		}

		// 6. Build ReceiptWithItems aggregate
		ReceiptWithItems receiptWithItems = new()
		{
			Receipt = receipt,
			Items = items,
			Adjustments = adjustments,
		};

		// 7. Compute waterfall splits
		YnabSplitResult splitResult = splitCalculator.ComputeWaterfallSplits(
			receiptWithItems, transactions, categoryToYnabId);

		// 8. Create YNAB transactions and track sync
		List<PushedTransactionInfo> pushedTransactions = [];
		Dictionary<(long Milliunits, DateOnly Date), int> importIdOccurrences = [];

		foreach (YnabTransactionSplit txSplit in splitResult.TransactionSplits)
		{
			Domain.Core.Transaction localTx = transactions.First(t => t.Id == txSplit.LocalTransactionId);
			string ynabAccountId = accountToYnabId[localTx.AccountId];

			// Compute import_id for deduplication
			(long Milliunits, DateOnly Date) importIdKey = (txSplit.TotalMilliunits, localTx.Date);
			int occurrence = importIdOccurrences.TryGetValue(importIdKey, out int current) ? current + 1 : 1;
			importIdOccurrences[importIdKey] = occurrence;
			string importId = YnabImportId.Generate(txSplit.TotalMilliunits, localTx.Date, occurrence);

			// Create sync record (Pending)
			YnabSyncRecordDto syncRecord = await syncRecordService.CreateAsync(
				localTx.Id, budgetId, YnabSyncType.TransactionPush, cancellationToken);

			try
			{
				// Build sub-transactions
				List<YnabSubTransaction>? subTransactions = null;
				string? categoryId = null;

				if (txSplit.SubTransactions.Count == 1)
				{
					// Single category — no split needed
					categoryId = txSplit.SubTransactions[0].YnabCategoryId;
				}
				else if (txSplit.SubTransactions.Count > 1)
				{
					subTransactions = txSplit.SubTransactions
						.Select(st => new YnabSubTransaction(st.Milliunits, st.YnabCategoryId, null))
						.ToList();
				}

				YnabCreateTransactionRequest ynabRequest = new(
					AccountId: ynabAccountId,
					Date: localTx.Date,
					Amount: txSplit.TotalMilliunits,
					Memo: $"Receipt: {receipt.Location} ({receipt.Date:yyyy-MM-dd})",
					PayeeName: receipt.Location,
					CategoryId: categoryId,
					Approved: false,
					SubTransactions: subTransactions,
					ImportId: importId);

				YnabCreateTransactionResponse ynabResponse = await ynabApiClient.CreateTransactionAsync(
					budgetId, ynabRequest, cancellationToken);

				// Update sync record to Synced
				await syncRecordService.UpdateStatusAsync(
					syncRecord.Id, YnabSyncStatus.Synced, ynabResponse.TransactionId, null, cancellationToken);

				pushedTransactions.Add(new PushedTransactionInfo(
					localTx.Id,
					ynabResponse.TransactionId,
					txSplit.TotalMilliunits,
					txSplit.SubTransactions.Count));
			}
			catch (Exception ex)
			{
				// Update sync record to Failed
				await syncRecordService.UpdateStatusAsync(
					syncRecord.Id, YnabSyncStatus.Failed, null, ex.Message, cancellationToken);

				return new PushYnabTransactionsResult(false, pushedTransactions,
					Error: $"Failed to create YNAB transaction for local transaction {localTx.Id}: {ex.Message}");
			}
		}

		return new PushYnabTransactionsResult(true, pushedTransactions);
	}
}
