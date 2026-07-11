using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Utilities;
using Common;
using Domain.Aggregates;
using Mediator;
using Microsoft.Extensions.Logging;

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
	IYnabSplitCalculator splitCalculator,
	IYnabSyncEventService ynabSyncEventService,
	IYnabResponseContext ynabResponseContext,
	ILogger<PushYnabTransactionsCommandHandler> logger) : IRequestHandler<PushYnabTransactionsCommand, PushYnabTransactionsResult>
{
	public async ValueTask<PushYnabTransactionsResult> Handle(PushYnabTransactionsCommand request, CancellationToken cancellationToken)
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

		// 5. Pre-load existing sync records (Synced → skip; Failed/Pending → reuse on retry)
		Dictionary<Guid, YnabSyncRecordDto> existingSyncRecords = [];
		foreach (Domain.Core.Transaction tx in transactions)
		{
			YnabSyncRecordDto? existingSync = await syncRecordService.GetByTransactionAndTypeAsync(
				tx.Id, YnabSyncType.TransactionPush, cancellationToken);
			if (existingSync is not null)
			{
				existingSyncRecords[tx.Id] = existingSync;
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
		YnabSplitResult splitResult;
		try
		{
			splitResult = splitCalculator.ComputeWaterfallSplits(
				receiptWithItems, transactions, categoryToYnabId);
		}
		catch (InvalidOperationException ex)
		{
			return new PushYnabTransactionsResult(false, [], Error: ex.Message);
		}

		// 8. Assign a stable import_id to EVERY split up front — including already-synced ones
		// (RECEIPTS-752). YNAB import_ids disambiguate transactions sharing amount+date via an
		// occurrence counter. If the counter only advanced for the splits actually pushed, a
		// retry (where a synced sibling is skipped) would recompute occurrence 1 for a still-
		// unsynced transaction with the same amount+date, colliding with the sibling's already-
		// consumed import_id. YNAB would 409 and recovery would bind both local transactions to
		// the sibling's single YNAB transaction, silently dropping the second amount. Counting
		// every split here keeps occurrence numbering deterministic across retries.
		Dictionary<(long Milliunits, DateOnly Date), int> importIdOccurrences = [];
		Dictionary<Guid, string> importIdByTransactionId = [];
		foreach (YnabTransactionSplit txSplit in splitResult.TransactionSplits)
		{
			Domain.Core.Transaction localTx = transactions.First(t => t.Id == txSplit.LocalTransactionId);
			(long Milliunits, DateOnly Date) importIdKey = (txSplit.TotalMilliunits, localTx.Date);
			int occurrence = importIdOccurrences.TryGetValue(importIdKey, out int current) ? current + 1 : 1;
			importIdOccurrences[importIdKey] = occurrence;
			importIdByTransactionId[txSplit.LocalTransactionId] = YnabImportId.Generate(
				txSplit.TotalMilliunits, localTx.Date, request.ReceiptId, occurrence);
		}

		// Track YNAB transaction ids already bound to a sync record for this receipt so recovery
		// can never double-bind two local transactions to one YNAB transaction (RECEIPTS-752).
		// Seed with the ids of already-synced siblings preserved from a prior push.
		HashSet<string> boundYnabTransactionIds = existingSyncRecords.Values
			.Where(r => r.SyncStatus == YnabSyncStatus.Synced && r.YnabTransactionId is not null)
			.Select(r => r.YnabTransactionId!)
			.ToHashSet();

		// 9. Create YNAB transactions and track sync
		List<PushedTransactionInfo> pushedTransactions = [];

		foreach (YnabTransactionSplit txSplit in splitResult.TransactionSplits)
		{
			Domain.Core.Transaction localTx = transactions.First(t => t.Id == txSplit.LocalTransactionId);

			existingSyncRecords.TryGetValue(localTx.Id, out YnabSyncRecordDto? existingRecord);

			// Skip already-synced transactions (allows retry after partial push)
			if (existingRecord?.SyncStatus == YnabSyncStatus.Synced)
			{
				continue;
			}

			string ynabAccountId = accountToYnabId[localTx.AccountId];

			// import_id was assigned deterministically in the first pass above so occurrence
			// numbering stays stable across retries.
			string importId = importIdByTransactionId[txSplit.LocalTransactionId];

			YnabSyncRecordDto? syncRecord = null;
			try
			{
				if (existingRecord is not null)
				{
					// Reuse existing Failed/Pending row to avoid unique-constraint violation
					await syncRecordService.UpdateStatusAsync(
						existingRecord.Id, YnabSyncStatus.Pending, null, null, cancellationToken);
					syncRecord = existingRecord;
				}
				else
				{
					// Create sync record (Pending) — inside try so DB failure is caught
					syncRecord = await syncRecordService.CreateAsync(
						localTx.Id, budgetId, YnabSyncType.TransactionPush, cancellationToken);
				}

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

				YnabCreateTransactionResponse ynabResponse;
				try
				{
					ynabResponse = await ynabApiClient.CreateTransactionAsync(
						budgetId, ynabRequest, cancellationToken);
				}
				catch (HttpRequestException conflictEx) when (conflictEx.StatusCode == System.Net.HttpStatusCode.Conflict)
				{
					string? recoveredId = await ynabApiClient.FindTransactionByImportIdAsync(
						budgetId, ynabAccountId, importId, localTx.Date.AddDays(-1), cancellationToken);

					if (recoveredId is null)
					{
						throw;
					}

					// Reject a recovered id already bound to another local transaction's sync
					// record for this receipt: binding it here would point two local transactions
					// at one YNAB transaction and silently drop this amount (RECEIPTS-752). Fail
					// loudly instead of reporting a false success. Add returns false if present.
					if (!boundYnabTransactionIds.Add(recoveredId))
					{
						string dupError = $"YNAB transaction '{recoveredId}' is already bound to another sync record " +
							$"for this receipt; refusing to double-bind local transaction {localTx.Id}.";

						await syncRecordService.UpdateStatusAsync(
							syncRecord!.Id, YnabSyncStatus.Failed, null, dupError, cancellationToken);

						await LogPushEventAsync(request.ReceiptId, localTx.Id, success: false, dupError, cancellationToken);

						return new PushYnabTransactionsResult(false, pushedTransactions, Error: dupError);
					}

					await syncRecordService.UpdateStatusAsync(
						syncRecord!.Id, YnabSyncStatus.Synced, recoveredId, null, cancellationToken);

					pushedTransactions.Add(new PushedTransactionInfo(
						localTx.Id,
						recoveredId,
						txSplit.TotalMilliunits,
						txSplit.SubTransactions.Count));

					await LogPushEventAsync(request.ReceiptId, localTx.Id, success: true, errorMessage: null, cancellationToken);
					continue;
				}

				// Record the freshly created YNAB id so a later split in this push cannot
				// recover-bind onto it (RECEIPTS-752 double-bind guard).
				boundYnabTransactionIds.Add(ynabResponse.TransactionId);

				await LogPushEventAsync(request.ReceiptId, localTx.Id, success: true, errorMessage: null, cancellationToken);

				// Update sync record to Synced — separate error handling (Bug 6)
				try
				{
					await syncRecordService.UpdateStatusAsync(
						syncRecord.Id, YnabSyncStatus.Synced, ynabResponse.TransactionId, null, cancellationToken);
				}
				catch (Exception statusEx)
				{
					// YNAB TX already created; don't mark as Failed. Return success with warning.
					pushedTransactions.Add(new PushedTransactionInfo(
						localTx.Id,
						ynabResponse.TransactionId,
						txSplit.TotalMilliunits,
						txSplit.SubTransactions.Count));

					return new PushYnabTransactionsResult(true, pushedTransactions,
						Error: $"YNAB transaction created but sync record update failed for transaction {localTx.Id}: {statusEx.Message}");
				}

				pushedTransactions.Add(new PushedTransactionInfo(
					localTx.Id,
					ynabResponse.TransactionId,
					txSplit.TotalMilliunits,
					txSplit.SubTransactions.Count));
			}
			catch (Exception ex)
			{
				// Mark sync record as Failed if it was created (preserves original behavior)
				if (syncRecord is not null)
				{
					await syncRecordService.UpdateStatusAsync(
						syncRecord.Id, YnabSyncStatus.Failed, null, ex.Message, cancellationToken);
				}

				await LogPushEventAsync(request.ReceiptId, localTx.Id, success: false, ex.Message, cancellationToken);

				return new PushYnabTransactionsResult(false, pushedTransactions,
					Error: $"Failed to push YNAB transaction for local transaction {localTx.Id}: {ex.Message}");
			}
		}

		return new PushYnabTransactionsResult(true, pushedTransactions);
	}

	// RECEIPTS-737: append one YnabSyncEvent per push attempt. Best-effort — a logging failure
	// must never fail the push itself, so swallow and warn. httpStatus/requestId come from the
	// transport-layer response context captured on the CreateTransaction call.
	private async Task LogPushEventAsync(Guid receiptId, Guid transactionId, bool success, string? errorMessage, CancellationToken cancellationToken)
	{
		try
		{
			await ynabSyncEventService.WriteAsync(
				YnabSyncEventType.Push,
				success,
				receiptId,
				transactionId,
				ynabResponseContext.LastStatusCode,
				errorMessage,
				ynabResponseContext.LastRequestId,
				cancellationToken);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to write YnabSyncEvent for receipt {ReceiptId} transaction {TransactionId}", receiptId, transactionId);
		}
	}
}
