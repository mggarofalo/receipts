using System.Net;
using System.Text.Json;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Ynab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Infrastructure.Tests.Services;

public class YnabApiClientFindByImportIdTests
{
	private static YnabApiClient CreateClient(
		HttpMessageHandler handler,
		string? pat = "test-pat",
		IMemoryCache? cache = null,
		IYnabRateLimitTracker? rateLimitTracker = null)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.ynab.com/v1/") };
		cache ??= new MemoryCache(new MemoryCacheOptions());
		rateLimitTracker ??= new Mock<IYnabRateLimitTracker>().Object;

		Dictionary<string, string?> configValues = new();
		if (pat is not null)
		{
			configValues["YNAB_PAT"] = pat;
		}

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(configValues)
			.Build();

		Mock<ILogger<YnabApiClient>> logger = new();
		return new YnabApiClient(httpClient, cache, configuration, rateLimitTracker, new YnabResponseContext(), logger.Object);
	}

	private static HttpMessageHandler CreateHandler(HttpStatusCode statusCode, string content)
	{
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.ReturnsAsync(new HttpResponseMessage
			{
				StatusCode = statusCode,
				Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
			});
		return handlerMock.Object;
	}

	private static string TransactionsListJson(params object[] transactions)
	{
		return JsonSerializer.Serialize(new
		{
			data = new
			{
				transactions,
				server_knowledge = 1L,
			}
		});
	}

	[Fact]
	public async Task MatchFound_ReturnsTransactionId()
	{
		string json = TransactionsListJson(
			new { id = "tx-1", date = "2026-04-01", amount = -10000L, memo = (string?)null, cleared = "cleared", approved = true, account_id = "acc-1", category_id = "cat-1", payee_name = "Store", import_id = "YNAB:-10000:2026-04-01:abc123:1", deleted = false });

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().Be("tx-1");
	}

	[Fact]
	public async Task NoMatch_WrongImportId_ReturnsNull()
	{
		string json = TransactionsListJson(
			new { id = "tx-1", date = "2026-04-01", amount = -10000L, memo = (string?)null, cleared = "cleared", approved = true, account_id = "acc-1", category_id = "cat-1", payee_name = "Store", import_id = "YNAB:-10000:2026-04-01:other:1", deleted = false });

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task NoMatch_WrongAccountId_ReturnsNull()
	{
		string json = TransactionsListJson(
			new { id = "tx-1", date = "2026-04-01", amount = -10000L, memo = (string?)null, cleared = "cleared", approved = true, account_id = "acc-2", category_id = "cat-1", payee_name = "Store", import_id = "YNAB:-10000:2026-04-01:abc123:1", deleted = false });

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task DeletedTransactionIgnored_ReturnsNull()
	{
		string json = TransactionsListJson(
			new { id = "tx-1", date = "2026-04-01", amount = -10000L, memo = (string?)null, cleared = "cleared", approved = true, account_id = "acc-1", category_id = "cat-1", payee_name = "Store", import_id = "YNAB:-10000:2026-04-01:abc123:1", deleted = true });

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task EmptyList_ReturnsNull()
	{
		string json = TransactionsListJson();

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().BeNull();
	}

	[Fact]
	public async Task NotFoundFromYnab_ReturnsNull()
	{
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.NotFound, "{}"));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "YNAB:-10000:2026-04-01:abc123:1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().BeNull();
	}
}
