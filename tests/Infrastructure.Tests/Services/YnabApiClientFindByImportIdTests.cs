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
	[Theory]
	[InlineData("[Receipts push:operation-1] receipt", "acc-1", false, "tx-1")]
	[InlineData("Bank memo [Receipts push:operation-1] receipt", "acc-1", false, "tx-1")]
	[InlineData("[Receipts push:operation-10] receipt", "acc-1", false, null)]
	[InlineData("[Receipts push:operation-1 receipt", "acc-1", false, null)]
	[InlineData("operation-1", "acc-1", false, null)]
	[InlineData("[Receipts push:operation-1] receipt", "acc-2", false, null)]
	[InlineData("[Receipts push:operation-1] receipt", "acc-1", true, null)]
	public async Task MemoRecovery_RequiresCompleteMarkerAndActiveMatchingAccount(
		string memo, string accountId, bool deleted, string? expected)
	{
		string json = TransactionsListJson(new
		{
			id = "tx-1",
			date = "2026-04-01",
			amount = -10000L,
			memo,
			cleared = "uncleared",
			approved = false,
			account_id = accountId,
			import_id = (string?)null,
			deleted,
		});
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "operation-1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().Be(expected);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MultipleDistinctRecoveryMatches_RejectsAmbiguousLink(bool legacyMatch)
	{
		string json = TransactionsListJson(
			new
			{
				id = "tx-1",
				date = "2026-04-01",
				amount = -10000L,
				memo = "[Receipts push:operation-1] receipt",
				account_id = "acc-1",
				deleted = false
			},
			new
			{
				id = "tx-2",
				date = "2026-04-01",
				amount = -10000L,
				memo = legacyMatch ? null : "[Receipts push:operation-1] receipt",
				import_id = legacyMatch ? "operation-1" : null,
				account_id = "acc-1",
				deleted = false
			});
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		Func<Task> action = () => client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "operation-1", new DateOnly(2026, 4, 1), CancellationToken.None);

		await action.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task SameTransactionMatchesLegacyIdentityAndMemo_ReturnsSingleLink()
	{
		string json = TransactionsListJson(new
		{
			id = "tx-1",
			date = "2026-04-01",
			amount = -10000L,
			memo = "[Receipts push:operation-1] receipt",
			import_id = "operation-1",
			account_id = "acc-1",
			deleted = false,
		});
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		string? result = await client.FindTransactionByImportIdAsync(
			"budget-1", "acc-1", "operation-1", new DateOnly(2026, 4, 1), CancellationToken.None);

		result.Should().Be("tx-1");
	}

}
