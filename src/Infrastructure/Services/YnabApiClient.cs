using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Ynab;
using Infrastructure.Ynab.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class YnabApiClient(
	HttpClient httpClient,
	IMemoryCache memoryCache,
	IConfiguration configuration,
	ILogger<YnabApiClient> logger) : IYnabApiClient
{
	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

	private const string BudgetsCacheKey = "ynab:budgets";
	private const string AccountsCacheKeyPrefix = "ynab:accounts:";
	private const string CategoriesCacheKeyPrefix = "ynab:categories:";

	private static readonly HashSet<string> ExcludedCategoryGroups = new(StringComparer.OrdinalIgnoreCase)
	{
		"Internal Master Category",
		"Credit Card Payments",
	};

	private readonly YnabClientOptions _options = new();

	private string? Pat => configuration[ConfigurationVariables.YnabPat];

	public bool IsConfigured => !string.IsNullOrWhiteSpace(Pat);

	public async Task<List<YnabBudget>> GetBudgetsAsync(CancellationToken cancellationToken)
	{
		if (memoryCache.TryGetValue(BudgetsCacheKey, out List<YnabBudget>? cached) && cached is not null)
		{
			return cached;
		}

		YnabBudgetsResponseEnvelope envelope = await GetAsync<YnabBudgetsResponseEnvelope>("budgets", cancellationToken);

		List<YnabBudget> budgets = envelope.Data.Budgets
			.Select(b => new YnabBudget(b.Id, b.Name))
			.ToList();

		memoryCache.Set(BudgetsCacheKey, budgets, TimeSpan.FromSeconds(_options.BudgetCacheTtlSeconds));
		return budgets;
	}

	public async Task<List<YnabAccount>> GetAccountsAsync(string budgetId, CancellationToken cancellationToken)
	{
		string cacheKey = $"{AccountsCacheKeyPrefix}{budgetId}";
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
		string cacheKey = $"{CategoriesCacheKeyPrefix}{budgetId}";
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
		return new YnabTransaction(
			t.Id,
			DateOnly.Parse(t.Date),
			t.Amount,
			t.Memo,
			t.ClearedStatus,
			t.Approved,
			t.AccountId,
			t.CategoryId,
			t.PayeeName);
	}

	public async Task<YnabCreateTransactionResponse> CreateTransactionAsync(
		string budgetId, YnabCreateTransactionRequest request, CancellationToken cancellationToken)
	{
		YnabSaveTransactionDto transactionDto = new()
		{
			AccountId = request.AccountId,
			Date = request.Date.ToString("yyyy-MM-dd"),
			Amount = request.Amount,
			Memo = request.Memo,
			PayeeName = request.PayeeName,
			CategoryId = request.CategoryId,
			Approved = request.Approved,
			ImportId = request.ImportId,
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

		YnabCreateTransactionResponseEnvelope envelope = await PostAsync<YnabSaveTransactionWrapper, YnabCreateTransactionResponseEnvelope>(
			$"budgets/{Uri.EscapeDataString(budgetId)}/transactions", body, cancellationToken);

		string transactionId = envelope.Data.TransactionIds.FirstOrDefault()
			?? envelope.Data.Transaction?.Id
			?? throw new InvalidOperationException("YNAB API did not return a transaction ID.");

		return new YnabCreateTransactionResponse(transactionId);
	}

	public async Task<List<YnabTransaction>> GetTransactionsByDateAsync(string budgetId, DateOnly sinceDate, CancellationToken cancellationToken)
	{
		string encodedBudgetId = Uri.EscapeDataString(budgetId);
		string formattedDate = sinceDate.ToString("yyyy-MM-dd");

		YnabTransactionsListResponseEnvelope envelope = await GetAsync<YnabTransactionsListResponseEnvelope>(
			$"budgets/{encodedBudgetId}/transactions?since_date={formattedDate}", cancellationToken);

		return envelope.Data.Transactions
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
		using HttpRequestMessage request = new(HttpMethod.Get, path);
		ApplyAuth(request);

		using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);

		T result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
			?? throw new InvalidOperationException($"Failed to deserialize YNAB response for {path}.");

		return result;
	}

	private async Task<TResponse> PostAsync<TBody, TResponse>(string path, TBody body, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, path);
		ApplyAuth(request);
		request.Content = JsonContent.Create(body, options: JsonOptions);

		using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);

		TResponse result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
			?? throw new InvalidOperationException($"Failed to deserialize YNAB response for POST {path}.");

		return result;
	}

	private async Task PatchAsync<TBody>(string path, TBody body, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Patch, path);
		ApplyAuth(request);
		request.Content = JsonContent.Create(body, options: JsonOptions);

		using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
	}

	private void ApplyAuth(HttpRequestMessage request)
	{
		string? pat = Pat;
		if (string.IsNullOrWhiteSpace(pat))
		{
			throw new YnabAuthException("YNAB personal access token is not configured.");
		}

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
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
				throw new YnabAuthException("YNAB authentication failed. Check your personal access token.");

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
