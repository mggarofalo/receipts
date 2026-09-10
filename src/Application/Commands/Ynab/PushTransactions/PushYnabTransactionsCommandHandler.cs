using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

		// 2. Resolve the destination before loading any destination-owned state.
		string? budgetId = request.CapturedYnabBudgetId
			?? await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return new PushYnabTransactionsResult(false, [], Error: "No YNAB budget selected.");
		}

		// 3. Check all categories are mapped for the selected budget (fail-fast)
		List<string> distinctCategories = items.Select(i => i.Category).Distinct().ToList();
		List<YnabCategoryMappingDto> allMappings = await categoryMappingService.GetByBudgetIdAsync(budgetId, cancellationToken);
		Dictionary<string, string> categoryToYnabId = allMappings
			.ToDictionary(m => m.ReceiptsCategory, m => m.YnabCategoryId);

		List<string> unmapped = distinctCategories.Where(c => !categoryToYnabId.ContainsKey(c)).ToList();
		if (unmapped.Count > 0)
		{
			return new PushYnabTransactionsResult(false, [], UnmappedCategories: unmapped, Error: "Unmapped categories found.");
		}

		// 4. Get account mappings for the transactions
		List<YnabAccountMappingDto> accountMappingsList = await accountMappingService.GetByBudgetIdAsync(budgetId, cancellationToken);
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
			YnabSyncRecordDto? existingSync = await syncRecordService.GetByTransactionTypeAndBudgetAsync(
				tx.Id, YnabSyncType.TransactionPush, budgetId, cancellationToken);
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
		string? snapshotWarning = null;

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

			// Materialize the complete remote request before acquiring a send claim. The
			// persisted snapshot, not mutable receipt state, is authoritative on retries.
			List<YnabSubTransaction>? subTransactions = null;
			string? categoryId = null;
			if (txSplit.SubTransactions.Count == 1)
			{
				categoryId = txSplit.SubTransactions[0].YnabCategoryId;
			}
			else if (txSplit.SubTransactions.Count > 1)
			{
				subTransactions = txSplit.SubTransactions
					.Select(st => new YnabSubTransaction(st.Milliunits, st.YnabCategoryId, null))
					.ToList();
			}

			YnabCreateTransactionRequest proposedRequest = new(
				AccountId: ynabAccountId,
				Date: localTx.Date,
				Amount: txSplit.TotalMilliunits,
				Memo: $"Receipt: {receipt.Location} ({receipt.Date:yyyy-MM-dd})",
				PayeeName: receipt.Location,
				CategoryId: categoryId,
				Approved: false,
				SubTransactions: subTransactions,
				ImportId: importId);

			YnabPushOperation operation;
			string proposedSourceVersion = ComputeSourceVersion(request.ReceiptId, localTx.Id, proposedRequest);
			try
			{
				operation = await syncRecordService.PreparePushOperationAsync(
					localTx.Id,
					budgetId,
					proposedRequest,
					proposedSourceVersion,
					cancellationToken);
			}
			catch (Exception ex)
			{
				return new PushYnabTransactionsResult(false, pushedTransactions,
					Error: $"Failed to persist the YNAB operation for local transaction {localTx.Id}: {ex.Message}");
			}

			if (operation.SyncStatus == YnabSyncStatus.Synced)
			{
				continue;
			}

			if (operation.SourceVersion.Length > 0 && operation.SourceVersion != proposedSourceVersion)
			{
				snapshotWarning = "The receipt changed after its original YNAB attempt. The saved operation snapshot was reconciled or retried without changing its import ID.";
			}

			if (operation.Request is null)
			{
				return new PushYnabTransactionsResult(false, pushedTransactions, Error: operation.LastError);
			}

			YnabPushOperationClaim? claim = await syncRecordService.TryClaimPushOperationAsync(
				operation.SyncRecordId,
				cancellationToken);
			if (claim is null)
			{
				return new PushYnabTransactionsResult(false, pushedTransactions,
					Error: $"A YNAB push for local transaction {localTx.Id} is already in progress. Refresh before retrying.");
			}

			YnabCreateTransactionRequest persistedRequest = claim.Operation.Request
				?? throw new InvalidOperationException($"Claimed YNAB operation {claim.Operation.SyncRecordId} has no request snapshot.");

			try
			{
				// Every retry first reconciles the immutable import ID. This covers a lost
				// response and a prior local status-write failure without issuing a second
				// create, even when the receipt was edited after the original attempt.
				if (claim.Operation.AttemptCount > 1)
				{
					string? reconciledId = await ynabApiClient.FindTransactionByImportIdAsync(
						budgetId,
						persistedRequest.AccountId,
						persistedRequest.ImportId!,
						persistedRequest.Date.AddDays(-1),
						cancellationToken);
					if (reconciledId is not null)
					{
						if (!boundYnabTransactionIds.Add(reconciledId))
						{
							string duplicateError = $"YNAB transaction '{reconciledId}' is already bound to another sync record " +
								$"for this receipt; refusing to double-bind local transaction {localTx.Id}.";
							await syncRecordService.CompletePushOperationAsync(
								operation.SyncRecordId, claim.ClaimToken, YnabSyncStatus.Failed, null, duplicateError, CancellationToken.None);
							await LogPushEventAsync(request.ReceiptId, localTx.Id, success: false, duplicateError, CancellationToken.None);
							return new PushYnabTransactionsResult(false, pushedTransactions, Error: duplicateError);
						}

						await syncRecordService.CompletePushOperationAsync(
							operation.SyncRecordId, claim.ClaimToken, YnabSyncStatus.Synced, reconciledId, null, CancellationToken.None);
						pushedTransactions.Add(new PushedTransactionInfo(
							localTx.Id, reconciledId, persistedRequest.Amount, persistedRequest.SubTransactions?.Count ?? 1));
						await LogPushEventAsync(request.ReceiptId, localTx.Id, success: true, errorMessage: null, CancellationToken.None);
						continue;
					}
				}

				YnabCreateTransactionResponse ynabResponse;
				try
				{
					ynabResponse = await ynabApiClient.CreateTransactionAsync(
						budgetId, persistedRequest, cancellationToken);
				}
				catch (HttpRequestException conflictEx) when (conflictEx.StatusCode == System.Net.HttpStatusCode.Conflict)
				{
					string? recoveredId = await ynabApiClient.FindTransactionByImportIdAsync(
						budgetId, persistedRequest.AccountId, persistedRequest.ImportId!, persistedRequest.Date.AddDays(-1), cancellationToken);

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

						await syncRecordService.CompletePushOperationAsync(
							operation.SyncRecordId, claim.ClaimToken, YnabSyncStatus.Failed, null, dupError, CancellationToken.None);

						await LogPushEventAsync(request.ReceiptId, localTx.Id, success: false, dupError, cancellationToken);

						return new PushYnabTransactionsResult(false, pushedTransactions, Error: dupError);
					}

					await syncRecordService.CompletePushOperationAsync(
						operation.SyncRecordId, claim.ClaimToken, YnabSyncStatus.Synced, recoveredId, null, CancellationToken.None);

					pushedTransactions.Add(new PushedTransactionInfo(
						localTx.Id,
						recoveredId,
						persistedRequest.Amount,
						persistedRequest.SubTransactions?.Count ?? 1));

					await LogPushEventAsync(request.ReceiptId, localTx.Id, success: true, errorMessage: null, CancellationToken.None);
					continue;
				}

				// Record the freshly created YNAB id so a later split in this push cannot
				// recover-bind onto it (RECEIPTS-752 double-bind guard).
				boundYnabTransactionIds.Add(ynabResponse.TransactionId);

				await LogPushEventAsync(request.ReceiptId, localTx.Id, success: true, errorMessage: null, CancellationToken.None);

				// Update sync record to Synced — separate error handling (Bug 6)
				try
				{
					bool completed = await syncRecordService.CompletePushOperationAsync(
						operation.SyncRecordId, claim.ClaimToken, YnabSyncStatus.Synced, ynabResponse.TransactionId, null, CancellationToken.None);
					if (!completed)
					{
						throw new InvalidOperationException("the persisted operation claim was lost before completion");
					}
				}
				catch (Exception statusEx)
				{
					// YNAB accepted the request, so never mark it Failed. Best-effort an
					// explicit Unknown transition; if persistence is still unavailable the
					// existing claim lease prevents an immediate duplicate send and the next
					// retry reconciles the immutable import ID.
					string statusError = $"YNAB transaction created but sync record update failed: {statusEx.Message}";
					try
					{
						await syncRecordService.CompletePushOperationAsync(
							operation.SyncRecordId,
							claim.ClaimToken,
							YnabSyncStatus.Unknown,
							ynabResponse.TransactionId,
							statusError,
							CancellationToken.None);
					}
					catch (Exception unknownStatusException)
					{
						logger.LogWarning(unknownStatusException,
							"Failed to persist Unknown for accepted YNAB operation {OperationId}", operation.SyncRecordId);
					}

					pushedTransactions.Add(new PushedTransactionInfo(
						localTx.Id,
						ynabResponse.TransactionId,
						persistedRequest.Amount,
						persistedRequest.SubTransactions?.Count ?? 1));

					return new PushYnabTransactionsResult(true, pushedTransactions,
						Error: $"YNAB transaction created but sync record update failed for transaction {localTx.Id}: {statusEx.Message}");
				}

				pushedTransactions.Add(new PushedTransactionInfo(
					localTx.Id,
					ynabResponse.TransactionId,
					persistedRequest.Amount,
					persistedRequest.SubTransactions?.Count ?? 1));
			}
			catch (Exception ex)
			{
				bool definitelyRejected = ex is HttpRequestException httpException &&
					httpException.StatusCode is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError &&
					httpException.StatusCode != System.Net.HttpStatusCode.RequestTimeout;
				YnabSyncStatus failureStatus = definitelyRejected ? YnabSyncStatus.Failed : YnabSyncStatus.Unknown;
				string operationError = failureStatus == YnabSyncStatus.Unknown
					? $"YNAB may have accepted the transaction: {ex.Message}. Retry to reconcile the saved import ID before another send."
					: ex.Message;
				try
				{
					await syncRecordService.CompletePushOperationAsync(
						operation.SyncRecordId, claim.ClaimToken, failureStatus, null, operationError, CancellationToken.None);
				}
				catch (Exception persistenceException)
				{
					logger.LogWarning(persistenceException,
						"Failed to persist {Status} for YNAB operation {OperationId}", failureStatus, operation.SyncRecordId);
				}

				await LogPushEventAsync(request.ReceiptId, localTx.Id, success: false, operationError, CancellationToken.None);

				return new PushYnabTransactionsResult(false, pushedTransactions,
					Error: $"Failed to push YNAB transaction for local transaction {localTx.Id}: {operationError}");
			}
		}

		return new PushYnabTransactionsResult(true, pushedTransactions, Error: snapshotWarning);
	}

	private static string ComputeSourceVersion(Guid receiptId, Guid transactionId, YnabCreateTransactionRequest request)
	{
		string source = $"{receiptId:N}:{transactionId:N}:{JsonSerializer.Serialize(request)}";
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
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
