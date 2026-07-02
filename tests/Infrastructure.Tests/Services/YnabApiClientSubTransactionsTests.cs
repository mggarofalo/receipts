using System.Net;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Infrastructure.Tests.Services;

public class YnabApiClientSubTransactionsTests
{
	private static YnabApiClient CreateClient(HttpMessageHandler handler)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.ynab.com/v1/") };
		IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
		Mock<IYnabRateLimitTracker> rateLimitTracker = new();

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["YNAB_PAT"] = "test-pat" })
			.Build();

		Mock<ILogger<YnabApiClient>> logger = new();
		return new YnabApiClient(httpClient, cache, configuration, rateLimitTracker.Object, new YnabResponseContext(), logger.Object);
	}

	private static HttpMessageHandler CreateHandler(string json)
	{
		Mock<HttpMessageHandler> handlerMock = new();
		handlerMock.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync",
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>())
			.ReturnsAsync(new HttpResponseMessage
			{
				StatusCode = HttpStatusCode.OK,
				Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
			});
		return handlerMock.Object;
	}

	[Fact]
	public async Task GetTransactionAsync_WithSubTransactions_MapsAll()
	{
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				transaction = new
				{
					id = "tx-1",
					date = "2026-04-01",
					amount = -30000,
					memo = "Walmart",
					cleared = "cleared",
					approved = true,
					account_id = "acc-1",
					category_id = (string?)null,
					category_name = (string?)null,
					payee_name = "Walmart",
					deleted = false,
					subtransactions = new[]
					{
						new
						{
							id = "sub-1",
							transaction_id = "tx-1",
							amount = -20000,
							memo = "Food",
							category_id = "cat-groceries",
							category_name = "Groceries",
							deleted = false,
						},
						new
						{
							id = "sub-2",
							transaction_id = "tx-1",
							amount = -10000,
							memo = "Cleaning",
							category_id = "cat-household",
							category_name = "Household",
							deleted = false,
						},
					}
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(json));

		YnabTransaction? result = await client.GetTransactionAsync("budget-1", "tx-1", CancellationToken.None);

		result.Should().NotBeNull();
		result!.Id.Should().Be("tx-1");
		result.SubTransactions.Should().NotBeNull();
		result.SubTransactions!.Should().HaveCount(2);
		result.SubTransactions[0].Id.Should().Be("sub-1");
		result.SubTransactions[0].Amount.Should().Be(-20000);
		result.SubTransactions[0].CategoryId.Should().Be("cat-groceries");
		result.SubTransactions[0].CategoryName.Should().Be("Groceries");
		result.SubTransactions[1].Id.Should().Be("sub-2");
		result.SubTransactions[1].CategoryName.Should().Be("Household");
	}

	[Fact]
	public async Task GetTransactionAsync_WithDeletedSubTransaction_FiltersOut()
	{
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				transaction = new
				{
					id = "tx-1",
					date = "2026-04-01",
					amount = -30000,
					memo = (string?)null,
					cleared = "cleared",
					approved = true,
					account_id = "acc-1",
					category_id = (string?)null,
					category_name = (string?)null,
					payee_name = (string?)null,
					deleted = false,
					subtransactions = new[]
					{
						new { id = "sub-1", transaction_id = "tx-1", amount = -20000, memo = (string?)null, category_id = "cat-a", category_name = "A", deleted = false },
						new { id = "sub-2", transaction_id = "tx-1", amount = -10000, memo = (string?)null, category_id = "cat-b", category_name = "B", deleted = true },
					}
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(json));

		YnabTransaction? result = await client.GetTransactionAsync("budget-1", "tx-1", CancellationToken.None);

		result.Should().NotBeNull();
		result!.SubTransactions.Should().NotBeNull();
		result.SubTransactions!.Should().HaveCount(1);
		result.SubTransactions[0].Id.Should().Be("sub-1");
	}

	[Fact]
	public async Task GetTransactionAsync_WithNoSubTransactions_ReturnsNullSubTransactions()
	{
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				transaction = new
				{
					id = "tx-1",
					date = "2026-04-01",
					amount = -5000,
					memo = (string?)null,
					cleared = "cleared",
					approved = true,
					account_id = "acc-1",
					category_id = "cat-1",
					category_name = "Groceries",
					payee_name = "Store",
					deleted = false,
					subtransactions = Array.Empty<object>(),
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(json));

		YnabTransaction? result = await client.GetTransactionAsync("budget-1", "tx-1", CancellationToken.None);

		result.Should().NotBeNull();
		result!.SubTransactions.Should().BeNull();
		result.CategoryId.Should().Be("cat-1");
		result.CategoryName.Should().Be("Groceries");
	}

	[Fact]
	public async Task GetTransactionAsync_WithAllSubTransactionsDeleted_ReturnsNullNotEmpty()
	{
		// Regression for a bug where filtering deleted subtransactions produced
		// an empty list instead of null, causing downstream handlers that check
		// `SubTransactions is { Count: > 0 }` to misclassify the transaction.
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				transaction = new
				{
					id = "tx-1",
					date = "2026-04-01",
					amount = -30000,
					memo = (string?)null,
					cleared = "cleared",
					approved = true,
					account_id = "acc-1",
					category_id = (string?)null,
					category_name = (string?)null,
					payee_name = (string?)null,
					deleted = false,
					subtransactions = new[]
					{
						new { id = "sub-1", transaction_id = "tx-1", amount = -20000, memo = (string?)null, category_id = "cat-a", category_name = "A", deleted = true },
						new { id = "sub-2", transaction_id = "tx-1", amount = -10000, memo = (string?)null, category_id = "cat-b", category_name = "B", deleted = true },
					}
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(json));

		YnabTransaction? result = await client.GetTransactionAsync("budget-1", "tx-1", CancellationToken.None);

		result.Should().NotBeNull();
		result!.SubTransactions.Should().BeNull();
	}
}
