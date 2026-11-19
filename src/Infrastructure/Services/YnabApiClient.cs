using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Infrastructure.Ynab;
using Infrastructure.Ynab.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Infrastructure.Services;

public class YnabApiClient(
	HttpClient httpClient,
	IMemoryCache memoryCache,
	IYnabCredentialSource credentialSource,
	IYnabRateLimitTracker rateLimitTracker,
	IYnabResponseContext responseContext,
	ILogger<YnabApiClient> logger) : IYnabApiClient
{
	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

	private const string CacheKeyPrefix = "ynab:";

	// Cached YNAB data is namespaced by credential identity so a changed credential never
	// serves the previous one's data.
	private async ValueTask<string> CachePrefixAsync(CancellationToken cancellationToken)
	{
		YnabBearerCredential? credential = await credentialSource.GetCredentialAsync(cancellationToken);
		return credential is null ? CacheKeyPrefix : $"{CacheKeyPrefix}{credential.CacheIdentity}:";
	}

	private static readonly HashSet<string> ExcludedCategoryGroups = new(StringComparer.OrdinalIgnoreCase)
	{
		"Internal Master Category",
		"Credit Card Payments",
	};

	private readonly YnabClientOptions _options = new();

	public async Task<List<YnabBudget>> GetBudgetsAsync(CancellationToken cancellationToken)
	{
		string cacheKey = $"{await CachePrefixAsync(cancellationToken)}budgets";
		if (memoryCache.TryGetValue(cacheKey, out List<YnabBudget>? cached) && cached is not null)
		{
			return cached;
		}

		YnabBudgetsResponseEnvelope envelope = await GetAsync<YnabBudgetsResponseEnvelope>("budgets", cancellationToken);

		List<YnabBudget> budgets = envelope.Data.Budgets
			.Select(b => new YnabBudget(b.Id, b.Name))
			.ToList();

		memoryCache.Set(cacheKey, budgets, TimeSpan.FromSeconds(_options.BudgetCacheTtlSeconds));
		return budgets;
	}

	public async Task<List<YnabAccount>> GetAccountsAsync(string budgetId, CancellationToken cancellationToken)
	{
		string cacheKey = $"{await CachePrefixAsync(cancellationToken)}accounts:{budgetId}";
		if (memoryCache.TryGetValue(cacheKey, out List<YnabAccount>? cached) && cached is not null)
		{
			return cached;
		}

		YnabAccountsResponseEnvelope envelope = await GetAsync<YnabAccountsResponseEnvelope>(
			$"budgets/{Uri.EscapeDataString(budgetId)}/accounts", cancellationToken);

		List<YnabAccount> accounts = envelope.Data.Accounts
			.Where(a => !a.Deleted)
			.Select(a => new YnabAccount(a.Id, a.Name, a.Type, a.OnBudget, a.Closed, a.Balance))
			.ToList();

		memoryCache.Set(cacheKey, accounts, TimeSpan.FromSeconds(_options.AccountCacheTtlSeconds));
		return accounts;
	}

	public async Task<List<YnabCategory>> GetCategoriesAsync(string budgetId, CancellationToken cancellationToken)
	{
		string cacheKey = $"{await CachePrefixAsync(cancellationToken)}categories:{budgetId}";
		if (memoryCache.TryGetValue(cacheKey, out List<YnabCategory>? cached) && cached is not null)
		{
			return cached;
		}

		YnabCategoriesResponseEnvelope envelope = await GetAsync<YnabCategoriesResponseEnvelope>(
			$"budgets/{Uri.EscapeDataString(budgetId)}/categories", cancellationToken);

		List<YnabCategory> categories = envelope.Data.CategoryGroups
			.Where(g => !g.Deleted && !ExcludedCategoryGroups.Contains(g.Name))
			.SelectMany(g => g.Categories
				.Where(c => !c.Deleted)
				.Select(c => new YnabCategory(
					c.Id,
					c.Name,
					c.CategoryGroupId,
					c.CategoryGroupName ?? g.Name,
					c.Hidden)))
			.ToList();

		memoryCache.Set(cacheKey, categories, TimeSpan.FromSeconds(_options.CategoryCacheTtlSeconds));
		return categories;
	}

	public async Task<YnabTransaction?> GetTransactionAsync(string budgetId, string transactionId, CancellationToken cancellationToken)
	{
		YnabTransactionResponseEnvelope envelope;
		try
		{
			envelope = await GetAsync<YnabTransactionResponseEnvelope>(
				$"budgets/{Uri.EscapeDataString(budgetId)}/transactions/{Uri.EscapeDataString(transactionId)}", cancellationToken);
		}
		catch (YnabNotFoundException)
		{
			return null;
		}

		YnabTransactionDto t = envelope.Data.Transaction;
		List<YnabSubTransactionRead>? subTransactions = null;
		if (t.SubTransactions is { Count: > 0 })
		{
			List<YnabSubTransactionRead> filtered = t.SubTransactions
				.Where(st => !st.Deleted)
				.Select(st => new YnabSubTransactionRead(
					st.Id,
					st.Amount,
					st.Memo,
					st.CategoryId,
					st.CategoryName))
				.ToList();
			subTransactions = filtered.Count > 0 ? filtered : null;
		}

		return new YnabTransaction(
			t.Id,
			DateOnly.Parse(t.Date),
			t.Amount,
			t.Memo,
			t.ClearedStatus,
			t.Approved,
			t.AccountId,
			t.CategoryId,
			t.PayeeName,
			t.CategoryName,
			subTransactions);
	}

	public async Task<YnabCreateTransactionResponse> CreateTransactionAsync(
		string budgetId, YnabCreateTransactionRequest request, CancellationToken cancellationToken)
	{
		YnabSaveTransactionDto transactionDto = new()
		{
			AccountId = request.AccountId,
			Date = request.Date.ToString("yyyy-MM-dd"),
			Amount = request.Amount,
			Memo = BuildPushMemo(request.Memo, request.ImportId),
			PayeeName = request.PayeeName,
			CategoryId = request.CategoryId,
			Approved = request.Approved,
			Cleared = "uncleared",
		};

		if (request.SubTransactions is { Count: > 0 })
		{
			transactionDto.SubTransactions = request.SubTransactions
				.Select(st => new YnabSaveSubTransactionDto
				{
					Amount = st.Amount,
					CategoryId = st.CategoryId,
					Memo = st.Memo,
				})
				.ToList();

			// YNAB requires CategoryId to be null on split parent
			transactionDto.CategoryId = null;
		}

		YnabSaveTransactionWrapper body = new() { Transaction = transactionDto };

		YnabCreateTransactionResponseEnvelope envelope;
		try
		{
			envelope = await PostAsync<YnabSaveTransactionWrapper, YnabCreateTransactionResponseEnvelope>(
				$"budgets/{Uri.EscapeDataString(budgetId)}/transactions", body, cancellationToken);
		}
		catch (BrokenCircuitException ex)
		{
			throw new YnabRequestNotSentException("The YNAB circuit is open; the transaction was not sent. Retry after the connection recovers.", ex);
		}

		string transactionId = envelope.Data.TransactionIds.FirstOrDefault()
			?? envelope.Data.Transaction?.Id
			?? throw new InvalidOperationException("YNAB API did not return a transaction ID.");

		return new YnabCreateTransactionResponse(transactionId);
	}

	public async Task<YnabTransactionsResult> GetTransactionsByDateAsync(string budgetId, DateOnly sinceDate, long? lastKnowledgeOfServer = null, CancellationToken cancellationToken = default)
	{
		string encodedBudgetId = Uri.EscapeDataString(budgetId);
		string formattedDate = sinceDate.ToString("yyyy-MM-dd");

		string url = $"budgets/{encodedBudgetId}/transactions?since_date={formattedDate}";
		if (lastKnowledgeOfServer.HasValue)
		{
			url += $"&last_knowledge_of_server={lastKnowledgeOfServer.Value}";
		}

		YnabTransactionsListResponseEnvelope envelope = await GetAsync<YnabTransactionsListResponseEnvelope>(
			url, cancellationToken);

		List<YnabTransaction> transactions = envelope.Data.Transactions
			.Where(t => !t.Deleted)
			.Select(t => new YnabTransaction(
				t.Id,
				DateOnly.Parse(t.Date),
				t.Amount,
				t.Memo,
				t.ClearedStatus,
				t.Approved,
				t.AccountId,
				t.CategoryId,
				t.PayeeName))
			.ToList();

		return new YnabTransactionsResult(transactions, envelope.Data.ServerKnowledge);
	}

	public async Task<string?> FindTransactionByImportIdAsync(string budgetId, string accountId, string importId, DateOnly sinceDate, CancellationToken cancellationToken)
	{
		string encodedBudgetId = Uri.EscapeDataString(budgetId);
		string formattedDate = sinceDate.ToString("yyyy-MM-dd");
		string url = $"budgets/{encodedBudgetId}/transactions?since_date={formattedDate}";

		YnabTransactionsListResponseEnvelope envelope;
		try
		{
			envelope = await GetAsync<YnabTransactionsListResponseEnvelope>(url, cancellationToken);
		}
		catch (YnabNotFoundException)
		{
			return null;
		}

		// ImportId remains the durable local operation key. Older sends used it as
		// a YNAB import_id; manual transactions carry the same key in their memo.
		string marker = PushMemoMarker(importId);
		List<string> matches = envelope.Data.Transactions
			.Where(t => !t.Deleted && t.AccountId == accountId &&
				(t.ImportId == importId || t.Memo?.Contains(marker, StringComparison.Ordinal) == true))
			.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList();
		if (matches.Count > 1)
		{
			throw new InvalidOperationException("Multiple YNAB transactions contain the saved operation reference. Review them before retrying.");
		}
		return matches.SingleOrDefault();
	}

	private static string PushMemoMarker(string operationId) => $"[Receipts push:{operationId}]";

	private static string? BuildPushMemo(string? memo, string? operationId)
	{
		if (string.IsNullOrWhiteSpace(operationId))
		{
			return memo;
		}

		// Keep the complete recovery reference at the start, even for long payees.
		string marker = PushMemoMarker(operationId);
		const int maxLength = 200;
		if (marker.Length > maxLength)
		{
			throw new ArgumentException("The YNAB operation reference exceeds the memo limit.", nameof(operationId));
		}
		string result = string.IsNullOrWhiteSpace(memo) ? marker : $"{marker} {memo}";
		return result.Length <= maxLength ? result : result[..maxLength];
	}

	public async Task UpdateTransactionMemoAsync(string budgetId, string transactionId, string memo, CancellationToken cancellationToken)
	{
		string encodedBudgetId = Uri.EscapeDataString(budgetId);
		string encodedTransactionId = Uri.EscapeDataString(transactionId);

		YnabUpdateTransactionWrapper body = new()
		{
			Transaction = new YnabUpdateTransactionDto { Memo = memo }
		};

		await PatchAsync<YnabUpdateTransactionWrapper>(
			$"budgets/{encodedBudgetId}/transactions/{encodedTransactionId}", body, cancellationToken);
	}

	private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);

		T result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
			?? throw new InvalidOperationException($"Failed to deserialize YNAB response for {path}.");

		return result;
	}

	private async Task<TResponse> PostAsync<TBody, TResponse>(string path, TBody body, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: JsonOptions) },
			cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);

		TResponse result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
			?? throw new InvalidOperationException($"Failed to deserialize YNAB response for POST {path}.");

		return result;
	}

	private async Task PatchAsync<TBody>(string path, TBody body, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body, options: JsonOptions) },
			cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
	}

	// Every YNAB request goes through here: one place attaches the credential and records the
	// response. The request is built by a factory because a message cannot be sent twice.
	private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
	{
		YnabBearerCredential credential = await credentialSource.GetCredentialAsync(cancellationToken)
			?? throw new YnabAuthException("YNAB is not connected. Connect YNAB or set a personal access token.");

		HttpResponseMessage response = await SendOnceAsync(createRequest, credential, cancellationToken);
		if (response.StatusCode != HttpStatusCode.Unauthorized)
		{
			return response;
		}

		// A 401 means YNAB did not apply the request, so sending it again is safe even for a
		// write. An OAuth access token can be renewed once; a personal access token cannot.
		YnabBearerCredential? renewed;
		try
		{
			renewed = await credentialSource.RenewAfterRejectionAsync(credential, cancellationToken);
		}
		catch
		{
			response.Dispose();
			throw;
		}

		if (renewed is null)
		{
			return response;
		}

		response.Dispose();
		return await SendOnceAsync(createRequest, renewed, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendOnceAsync(
		Func<HttpRequestMessage> createRequest, YnabBearerCredential credential, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = createRequest();
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);

		HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		rateLimitTracker.RecordRequest();
		CaptureResponseMetadata(response);
		return response;
	}

	// Parse YNAB's X-Rate-Limit ("used/limit") into the tracker and record status + request id
	// (when present) for the current request scope, so callers can attach them to a YnabSyncEvent.
	private void CaptureResponseMetadata(HttpResponseMessage response)
	{
		if (response.Headers.TryGetValues("X-Rate-Limit", out IEnumerable<string>? rateLimitValues))
		{
			string[]? parts = rateLimitValues.FirstOrDefault()?.Split('/', 2);
			if (parts is { Length: 2 }
				&& int.TryParse(parts[0], out int used)
				&& int.TryParse(parts[1], out int limit))
			{
				rateLimitTracker.RecordServerRateLimit(used, limit);
			}
		}

		string? requestId = response.Headers.TryGetValues("X-Request-Id", out IEnumerable<string>? requestIdValues)
			? requestIdValues.FirstOrDefault()
			: null;

		responseContext.Record((int)response.StatusCode, requestId);
	}

	private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		string body = await response.Content.ReadAsStringAsync(cancellationToken);
		string truncatedBody = body.Length > 500 ? body[..500] : body;

		switch (response.StatusCode)
		{
			case HttpStatusCode.Unauthorized:
				logger.LogWarning("YNAB API returned 401 Unauthorized");
				throw new YnabAuthException("YNAB authentication failed. Reconnect YNAB or check your personal access token.");

			case HttpStatusCode.NotFound:
				logger.LogWarning("YNAB API returned 404: {Body}", truncatedBody);
				throw new YnabNotFoundException($"YNAB resource not found.");

			default:
				logger.LogError("YNAB API returned {StatusCode}: {Body}", (int)response.StatusCode, truncatedBody);
				throw new HttpRequestException(
					$"YNAB API request failed with status {(int)response.StatusCode}.",
					null,
					response.StatusCode);
		}
	}
}
