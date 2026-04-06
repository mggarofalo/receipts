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

	public async Task<List<YnabMemoSyncResult>> SyncMemosByReceiptAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return [new YnabMemoSyncResult(Guid.Empty, receiptId, YnabMemoSyncOutcome.Failed, null, "No YNAB budget selected.", null)];
		}

		ReceiptEntity? receipt = await receiptRepository.GetByIdAsync(receiptId, cancellationToken);
		if (receipt is null)
		{
			return [new YnabMemoSyncResult(Guid.Empty, receiptId, YnabMemoSyncOutcome.Failed, null, "Receipt not found.", null)];
		}

		List<TransactionEntity> transactions = await transactionRepository.GetWithAccountByReceiptIdAsync(receiptId, cancellationToken);
		if (transactions.Count == 0)
		{
			return [];
		}

		List<YnabMemoSyncResult> results = [];
		foreach (TransactionEntity transaction in transactions)
		{
			YnabMemoSyncResult result = await SyncSingleTransactionAsync(transaction, receipt, budgetId, cancellationToken);
			results.Add(result);
		}

		return results;
	}

	public async Task<List<YnabMemoSyncResult>> SyncMemosBulkAsync(List<Guid> receiptIds, CancellationToken cancellationToken)
	{
		List<YnabMemoSyncResult> allResults = [];
		foreach (Guid receiptId in receiptIds)
		{
			List<YnabMemoSyncResult> results = await SyncMemosByReceiptAsync(receiptId, cancellationToken);
			allResults.AddRange(results);
		}

		return allResults;
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

	private async Task<YnabMemoSyncResult> SyncSingleTransactionAsync(
		TransactionEntity transaction, ReceiptEntity receipt, string budgetId, CancellationToken cancellationToken)
	{
		// Currency guard: V1 supports only USD
		if (transaction.AmountCurrency != Currency.USD)
		{
			logger.LogWarning("Skipping non-USD transaction {TransactionId} with currency {Currency}", transaction.Id, transaction.AmountCurrency);
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.CurrencySkipped, null, $"Non-USD currency: {transaction.AmountCurrency}", null);
		}

		// Check if already synced
		YnabSyncRecordDto? existingRecord = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, cancellationToken);
		if (existingRecord is not null && existingRecord.SyncStatus == YnabSyncStatus.Synced)
		{
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.AlreadySynced, existingRecord.YnabTransactionId, null, null);
		}

		// Fetch YNAB transactions for the same date
		List<YnabTransaction> ynabTransactions;
		try
		{
			ynabTransactions = await ynabClient.GetTransactionsByDateAsync(budgetId, transaction.Date, cancellationToken);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to fetch YNAB transactions for date {Date}", transaction.Date);
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Failed, null, $"Failed to fetch YNAB transactions: {ex.Message}", null);
		}

		// Convert local amount to YNAB milliunits (negated: local positive = YNAB outflow negative)
		long expectedYnabAmount = -YnabConvert.ToMilliunits(transaction.Amount);

		// Filter by exact date and exact negated amount
		List<YnabTransaction> candidates = ynabTransactions
			.Where(yt => yt.Date == transaction.Date && yt.Amount == expectedYnabAmount)
			.ToList();

		if (candidates.Count == 0)
		{
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.NoMatch, null, null, null);
		}

		// Filter out reconciled transactions — users consider them finalized
		List<YnabTransaction> nonReconciled = candidates
			.Where(yt => !string.Equals(yt.ClearedStatus, ReconciledClearedStatus, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (nonReconciled.Count == 0)
		{
			// All date+amount matches were reconciled
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.ReconciledSkipped, null, "All matching YNAB transactions are reconciled.", null);
		}

		candidates = nonReconciled;

		// Apply fuzzy payee matching
		string payeeName = receipt.Location;
		List<YnabTransaction> fuzzyMatches = candidates
			.Where(yt => IsPayeeMatch(payeeName, yt.PayeeName))
			.ToList();

		if (fuzzyMatches.Count == 1)
		{
			return await UpdateMemoAndTrackAsync(transaction, receipt, budgetId, fuzzyMatches[0], cancellationToken);
		}

		if (fuzzyMatches.Count > 1)
		{
			// Ambiguous — return candidates for user resolution
			List<YnabTransactionCandidate> ambiguousCandidates = fuzzyMatches
				.Select(yt => new YnabTransactionCandidate(yt.Id, yt.Date, yt.Amount, yt.Memo, yt.PayeeName, yt.AccountId))
				.ToList();
			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Ambiguous, null, null, ambiguousCandidates);
		}

		// No fuzzy match but had date+amount matches — still ambiguous if multiple, no match if zero
		if (candidates.Count == 1)
		{
			// Single date+amount match even without fuzzy payee — use it
			return await UpdateMemoAndTrackAsync(transaction, receipt, budgetId, candidates[0], cancellationToken);
		}

		// Multiple date+amount matches, no fuzzy payee match — ambiguous
		List<YnabTransactionCandidate> dateCandidates = candidates
			.Select(yt => new YnabTransactionCandidate(yt.Id, yt.Date, yt.Amount, yt.Memo, yt.PayeeName, yt.AccountId))
			.ToList();
		return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.Ambiguous, null, null, dateCandidates);
	}

	private async Task<YnabMemoSyncResult> UpdateMemoAndTrackAsync(
		TransactionEntity transaction, ReceiptEntity receipt, string budgetId,
		YnabTransaction ynabTransaction, CancellationToken cancellationToken)
	{
		string receiptLink = $"/receipts/{receipt.Id}";

		// Idempotency: if memo already contains this receipt link, skip
		if (ynabTransaction.Memo is not null && ynabTransaction.Memo.Contains(receiptLink))
		{
			// Ensure we have a sync record for tracking
			YnabSyncRecordDto? existing = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, cancellationToken);
			if (existing is null)
			{
				YnabSyncRecordDto record = await syncRecordService.CreateAsync(transaction.Id, budgetId, YnabSyncType.MemoUpdate, cancellationToken);
				await syncRecordService.UpdateStatusAsync(record.Id, YnabSyncStatus.Synced, ynabTransaction.Id, null, cancellationToken);
			}

			return new YnabMemoSyncResult(transaction.Id, receipt.Id, YnabMemoSyncOutcome.AlreadySynced, ynabTransaction.Id, null, null);
		}

		// Build new memo
		string newMemo = FormatMemo(ynabTransaction.Memo, receiptLink);

		// Create sync record as pending
		YnabSyncRecordDto? syncRecord = await syncRecordService.GetByTransactionAndTypeAsync(transaction.Id, YnabSyncType.MemoUpdate, cancellationToken);
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
		if (string.IsNullOrWhiteSpace(ynabPayeeName))
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
