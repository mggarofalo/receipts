using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Utilities;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class YnabMemoSyncService(
	IYnabApiClient ynabClient,
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabAccountMappingService accountMappingService,
	IYnabSyncRecordService syncRecordService,
	ITransactionRepository transactionRepository,
	IReceiptRepository receiptRepository,
	ILogger<YnabMemoSyncService> logger) : IYnabMemoSyncService
{
	internal const int YnabMemoMaxLength = 200;
	internal const string MemoSeparator = " | ";
	internal const string ReceiptLinkPrefix = "Receipt: /receipts/";
	internal const double FuzzyMatchThreshold = 0.3;
	internal const string ReconciledClearedStatus = "reconciled";

	public Task<List<YnabMemoSyncResult>> SyncMemosByReceiptAsync(Guid receiptId, CancellationToken cancellationToken)
		=> SyncOperationAsync([receiptId], cancellationToken);

	public Task<List<YnabMemoSyncResult>> SyncMemosBulkAsync(List<Guid> receiptIds, CancellationToken cancellationToken)
		=> SyncOperationAsync(receiptIds, cancellationToken);

	private sealed record MemoWork(TransactionEntity Transaction, ReceiptEntity Receipt,
		YnabSyncRecordDto? MemoRecord, YnabSyncRecordDto? PushRecord);

	private sealed record MemoPlan(MemoWork Work, YnabMemoSyncResult Result, YnabTransaction? AutomaticCandidate = null);

	private async Task<List<YnabMemoSyncResult>> SyncOperationAsync(List<Guid> receiptIds, CancellationToken cancellationToken)
	{
		List<Guid> uniqueReceiptIds = receiptIds.Distinct().ToList();
		if (uniqueReceiptIds.Count == 0)
		{
			return [];
		}

		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return uniqueReceiptIds.Select(id => new YnabMemoSyncResult(Guid.Empty, id,
				YnabMemoSyncOutcome.Failed, null, "No YNAB budget selected.", null)).ToList();
		}

		// Capture account identity once for the entire operation, including bulk requests.
		ILookup<Guid, YnabAccountMappingDto> mappings = (await accountMappingService.GetAllAsync(cancellationToken))
			.Where(mapping => string.Equals(mapping.YnabBudgetId, budgetId, StringComparison.Ordinal))
			.ToLookup(mapping => mapping.ReceiptsAccountId);
		List<YnabMemoSyncResult> results = [];
		List<MemoWork> work = [];
		HashSet<Guid> localIds = [];
		Dictionary<string, HashSet<Guid>> owners = new(StringComparer.Ordinal);
		foreach (Guid receiptId in uniqueReceiptIds)
		{
			ReceiptEntity? receipt = await receiptRepository.GetByIdAsync(receiptId, cancellationToken);
			if (receipt is null)
			{
				results.Add(new(Guid.Empty, receiptId, YnabMemoSyncOutcome.Failed, null, "Receipt not found.", null));
				continue;
			}

			foreach (TransactionEntity transaction in await transactionRepository.GetWithAccountByReceiptIdAsync(receiptId, cancellationToken))
			{
				if (!localIds.Add(transaction.Id))
				{
					continue;
				}

				YnabSyncRecordDto? memo = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, cancellationToken);
				YnabSyncRecordDto? push = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.TransactionPush, cancellationToken);
				work.Add(new(transaction, receipt, memo, push));
				// Preload even later AlreadySynced/Pending/Failed bindings before choosing any target.
				foreach (YnabSyncRecordDto? record in new[] { memo, push })
				{
					if (record is not null && record.YnabBudgetId == budgetId && !string.IsNullOrWhiteSpace(record.YnabTransactionId))
					{
						Reserve(owners, record.YnabTransactionId, transaction.Id);
					}
				}
			}
		}

		Dictionary<DateOnly, (List<YnabTransaction>? Transactions, string? Error)> byDate = [];
		foreach (DateOnly date in work.Select(item => item.Transaction.Date).Distinct())
		{
			try
			{
				YnabTransactionsResult fetched = await ynabClient.GetTransactionsByDateAsync(budgetId, date, cancellationToken: cancellationToken);
				byDate[date] = (fetched.Transactions, null);
				// Date-filtered knowledge is not a complete budget snapshot (RECEIPTS-523).
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to fetch YNAB transactions for date {Date}", date);
				byDate[date] = (null, ex.Message);
			}
		}

		List<MemoPlan> plans = work.Select(item => PlanMemo(item, budgetId, mappings, byDate, owners)).ToList();
		// Reserve every confident choice before any outbound write. Equally competing choices
		// remain unresolved; neither receipt order nor failure of an earlier PATCH breaks a tie.
		foreach (MemoPlan plan in plans.Where(plan => plan.AutomaticCandidate is not null))
		{
			Reserve(owners, plan.AutomaticCandidate!.Id, plan.Work.Transaction.Id);
		}

		foreach (MemoPlan plan in plans)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (plan.AutomaticCandidate is not { } candidate)
			{
				results.Add(plan.Result);
			}
			else if (owners[candidate.Id].Count > 1)
			{
				results.Add(Unresolved(plan.Work, [candidate], "Multiple local payments match this YNAB transaction. Confirm the intended payment."));
			}
			else
			{
				results.Add(await UpdateMemoAndTrackAsync(plan.Work.Transaction, plan.Work.Receipt, budgetId, candidate, cancellationToken));
			}
		}

		return results;
	}

	private static void Reserve(Dictionary<string, HashSet<Guid>> owners, string remoteId, Guid localId)
	{
		if (!owners.TryGetValue(remoteId, out HashSet<Guid>? localOwners))
		{
			localOwners = [];
			owners.Add(remoteId, localOwners);
		}
		localOwners.Add(localId);
	}

	private static YnabMemoSyncResult Unresolved(MemoWork work, List<YnabTransaction> candidates, string? error = null)
		=> new(work.Transaction.Id, work.Receipt.Id, YnabMemoSyncOutcome.Ambiguous, null, error,
			candidates.Select(candidate => new YnabTransactionCandidate(candidate.Id, candidate.Date, candidate.Amount,
				candidate.Memo, candidate.PayeeName, candidate.AccountId)).ToList());

	private static MemoPlan PlanMemo(MemoWork work, string budgetId, ILookup<Guid, YnabAccountMappingDto> mappings,
		Dictionary<DateOnly, (List<YnabTransaction>? Transactions, string? Error)> byDate,
		Dictionary<string, HashSet<Guid>> owners)
	{
		TransactionEntity transaction = work.Transaction;
		MemoPlan Outcome(YnabMemoSyncOutcome outcome, string? error = null, string? remoteId = null)
			=> new(work, new(transaction.Id, work.Receipt.Id, outcome, remoteId, error, null));

		if (transaction.AmountCurrency != Currency.USD)
		{
			return Outcome(YnabMemoSyncOutcome.CurrencySkipped, $"Non-USD currency: {transaction.AmountCurrency}");
		}

		YnabSyncRecordDto?[] records = [work.MemoRecord, work.PushRecord];
		if (records.Any(record => record is not null && record.YnabBudgetId != budgetId))
		{
			return Outcome(YnabMemoSyncOutcome.Failed, "An existing sync record belongs to another YNAB budget. Resolve that binding before syncing.");
		}
		if (work.MemoRecord is { SyncStatus: YnabSyncStatus.Synced } synced)
		{
			return Outcome(YnabMemoSyncOutcome.AlreadySynced, remoteId: synced.YnabTransactionId);
		}

		List<YnabAccountMappingDto> accountMappings = transaction.Card is null ? [] : mappings[transaction.Card.AccountId].ToList();
		if (accountMappings.Count != 1 || string.IsNullOrWhiteSpace(accountMappings[0].YnabAccountId))
		{
			return Outcome(YnabMemoSyncOutcome.Failed, "Map this payment's account to exactly one account in the selected YNAB budget before syncing.");
		}
		if (!byDate.TryGetValue(transaction.Date, out var fetched) || fetched.Transactions is null)
		{
			return Outcome(YnabMemoSyncOutcome.Failed, fetched.Error is null
				? "Failed to fetch YNAB transactions for this date." : $"Failed to fetch YNAB transactions: {fetched.Error}");
		}

		long amount = -YnabConvert.ToMilliunits(transaction.Amount);
		List<YnabTransaction> candidates = fetched.Transactions.Where(candidate =>
			string.Equals(candidate.AccountId, accountMappings[0].YnabAccountId, StringComparison.Ordinal)
			&& candidate.Date == transaction.Date && candidate.Amount == amount).DistinctBy(candidate => candidate.Id).ToList();
		if (candidates.Count == 0)
		{
			return Outcome(YnabMemoSyncOutcome.NoMatch);
		}
		candidates = candidates.Where(candidate => !string.Equals(candidate.ClearedStatus, ReconciledClearedStatus, StringComparison.OrdinalIgnoreCase)).ToList();
		if (candidates.Count == 0)
		{
			return Outcome(YnabMemoSyncOutcome.ReconciledSkipped, "All matching YNAB transactions are reconciled.");
		}

		List<YnabTransaction> payeeMatches = candidates.Where(candidate => IsPayeeMatch(work.Receipt.Location, candidate.PayeeName)).ToList();
		if (payeeMatches.Count != 1)
		{
			return new(work, Unresolved(work, payeeMatches.Count > 0 ? payeeMatches : candidates));
		}

		YnabTransaction selected = payeeMatches[0];
		if (records.Any(record => !string.IsNullOrWhiteSpace(record?.YnabTransactionId) && record.YnabTransactionId != selected.Id))
		{
			return Outcome(YnabMemoSyncOutcome.Failed, "This payment is already bound to another YNAB transaction. Resolve that binding before syncing.");
		}
		if (owners.TryGetValue(selected.Id, out HashSet<Guid>? existingOwners) && existingOwners.Any(id => id != transaction.Id))
		{
			return new(work, Unresolved(work, [selected], "This YNAB transaction is already bound to another participating local payment."));
		}

		return new(work, Unresolved(work, [selected]), selected);
	}

	public async Task<YnabMemoSyncResult> ResolveMemoSyncAsync(Guid localTransactionId, string ynabTransactionId, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return new YnabMemoSyncResult(localTransactionId, Guid.Empty, YnabMemoSyncOutcome.Failed, null, "No YNAB budget selected.", null);
		}

		TransactionEntity? transaction = await transactionRepository.GetByIdAsync(localTransactionId, cancellationToken);
		if (transaction is null)
		{
			return new YnabMemoSyncResult(localTransactionId, Guid.Empty, YnabMemoSyncOutcome.Failed, null, "Local transaction not found.", null);
		}

		ReceiptEntity? receipt = await receiptRepository.GetByIdAsync(transaction.ReceiptId, cancellationToken);
		if (receipt is null)
		{
			return new YnabMemoSyncResult(localTransactionId, transaction.ReceiptId, YnabMemoSyncOutcome.Failed, null, "Receipt not found.", null);
		}

		YnabTransaction? ynabTx = await ynabClient.GetTransactionAsync(budgetId, ynabTransactionId, cancellationToken);
		if (ynabTx is null)
		{
			return new YnabMemoSyncResult(localTransactionId, transaction.ReceiptId, YnabMemoSyncOutcome.Failed, null, "YNAB transaction not found.", null);
		}

		if (string.Equals(ynabTx.ClearedStatus, ReconciledClearedStatus, StringComparison.OrdinalIgnoreCase))
		{
			return new YnabMemoSyncResult(localTransactionId, transaction.ReceiptId, YnabMemoSyncOutcome.ReconciledSkipped, ynabTransactionId, "YNAB transaction is reconciled.", null);
		}

		return await UpdateMemoAndTrackAsync(transaction, receipt, budgetId, ynabTx, cancellationToken);
	}

	private async Task<YnabMemoSyncResult> UpdateMemoAndTrackAsync(
		TransactionEntity transaction, ReceiptEntity receipt, string budgetId,
		YnabTransaction ynabTransaction, CancellationToken cancellationToken)
	{
		// The current schema identifies records by local payment/type. Never overwrite a
		// binding for another budget; destination-scoped recovery is owned by RECEIPTS-961/962.
		YnabSyncRecordDto? syncRecord = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, cancellationToken);
		if (syncRecord is not null && syncRecord.YnabBudgetId != budgetId)
		{
			return new(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Failed, null,
				"An existing sync record belongs to another YNAB budget. Resolve that binding before syncing.", null);
		}

		string receiptLink = $"/receipts/{receipt.Id}";

		// Idempotency: if memo already contains this receipt link, skip
		if (ynabTransaction.Memo is not null && ynabTransaction.Memo.Contains(receiptLink))
		{
			// Ensure we have a sync record for tracking
			if (syncRecord is null)
			{
				YnabSyncRecordDto record = await syncRecordService.CreateAsync(transaction.Id, budgetId, YnabSyncType.MemoUpdate, cancellationToken);
				await syncRecordService.UpdateStatusAsync(record.Id, YnabSyncStatus.Synced, ynabTransaction.Id, null, cancellationToken);
			}

			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.AlreadySynced, ynabTransaction.Id, null, null);
		}

		// Build new memo
		string newMemo = FormatMemo(ynabTransaction.Memo, receiptLink);

		// Create sync record as pending
		if (syncRecord is null)
		{
			syncRecord = await syncRecordService.CreateAsync(transaction.Id, budgetId, YnabSyncType.MemoUpdate, cancellationToken);
		}

		try
		{
			await ynabClient.UpdateTransactionMemoAsync(budgetId, ynabTransaction.Id, newMemo, cancellationToken);
			await syncRecordService.UpdateStatusAsync(syncRecord.Id, YnabSyncStatus.Synced, ynabTransaction.Id, null, cancellationToken);

			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Synced, ynabTransaction.Id, null, null);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to update YNAB memo for transaction {YnabTransactionId}", ynabTransaction.Id);
			await syncRecordService.UpdateStatusAsync(syncRecord.Id, YnabSyncStatus.Failed, null, ex.Message, cancellationToken);

			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Failed, null, ex.Message, null);
		}
	}

	internal static string FormatMemo(string? existingMemo, string receiptLink)
	{
		string receiptTag = $"Receipt: {receiptLink}";

		if (string.IsNullOrWhiteSpace(existingMemo))
		{
			return receiptTag.Length > YnabMemoMaxLength
				? receiptTag[..YnabMemoMaxLength]
				: receiptTag;
		}

		string combined = $"{existingMemo}{MemoSeparator}{receiptTag}";
		if (combined.Length <= YnabMemoMaxLength)
		{
			return combined;
		}

		// Truncate existing memo to fit the receipt tag
		int receiptTagWithSeparatorLength = MemoSeparator.Length + receiptTag.Length;
		int availableForExisting = YnabMemoMaxLength - receiptTagWithSeparatorLength;

		if (availableForExisting <= 0)
		{
			// Receipt tag alone exceeds limit — just use the tag truncated
			return receiptTag[..YnabMemoMaxLength];
		}

		string truncatedExisting = existingMemo[..availableForExisting];
		return $"{truncatedExisting}{MemoSeparator}{receiptTag}";
	}

	internal static bool IsPayeeMatch(string receiptLocation, string? ynabPayeeName)
	{
		if (string.IsNullOrWhiteSpace(receiptLocation) || string.IsNullOrWhiteSpace(ynabPayeeName))
		{
			return false;
		}

		string normalizedReceipt = receiptLocation.Trim().ToUpperInvariant();
		string normalizedYnab = ynabPayeeName.Trim().ToUpperInvariant();

		// Exact match
		if (normalizedReceipt == normalizedYnab)
		{
			return true;
		}

		// Contains check (one contains the other)
		if (normalizedReceipt.Contains(normalizedYnab) || normalizedYnab.Contains(normalizedReceipt))
		{
			return true;
		}

		// Trigram similarity
		double similarity = TrigramSimilarity(normalizedReceipt, normalizedYnab);
		return similarity >= FuzzyMatchThreshold;
	}

	internal static double TrigramSimilarity(string a, string b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
		{
			return 0.0;
		}

		HashSet<string> trigramsA = GetTrigrams(a);
		HashSet<string> trigramsB = GetTrigrams(b);

		if (trigramsA.Count == 0 && trigramsB.Count == 0)
		{
			return 1.0; // Both empty after padding = identical
		}

		int intersection = trigramsA.Count(t => trigramsB.Contains(t));
		int union = trigramsA.Count + trigramsB.Count - intersection;

		return union == 0 ? 0.0 : (double)intersection / union;
	}

	private static HashSet<string> GetTrigrams(string s)
	{
		// Pad with spaces like pg_trgm does
		string padded = $"  {s} ";
		HashSet<string> trigrams = [];
		for (int i = 0; i <= padded.Length - 3; i++)
		{
			trigrams.Add(padded.Substring(i, 3));
		}

		return trigrams;
	}
}
