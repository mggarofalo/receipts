using System.Net;
using System.Text.Json;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Ynab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Infrastructure.Tests.Services;

public class YnabApiClientTests
{
	private readonly Mock<ILogger<YnabApiClient>> _loggerMock = new();

	private static YnabApiClient CreateClient(
		HttpMessageHandler handler,
		string? pat = "test-pat",
		IMemoryCache? cache = null)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.ynab.com/v1/") };
		cache ??= new MemoryCache(new MemoryCacheOptions());

		Dictionary<string, string?> configValues = new();
		if (pat is not null)
		{
			configValues["YNAB_PAT"] = pat;
		}

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(configValues)
			.Build();

		Mock<ILogger<YnabApiClient>> logger = new();
		return new YnabApiClient(httpClient, cache, configuration, logger.Object);
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

	[Fact]
	public async Task GetBudgetsAsync_DeserializesBudgetList()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				budgets = new[]
				{
					new { id = "budget-1", name = "My Budget" },
					new { id = "budget-2", name = "Other Budget" },
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		// Act
		var budgets = await client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		budgets.Should().HaveCount(2);
		budgets[0].Id.Should().Be("budget-1");
		budgets[0].Name.Should().Be("My Budget");
		budgets[1].Id.Should().Be("budget-2");
		budgets[1].Name.Should().Be("Other Budget");
	}

	[Fact]
	public async Task GetBudgetsAsync_401_ThrowsYnabAuthException()
	{
		// Arrange
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.Unauthorized, "{}"));

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
	}

	[Fact]
	public async Task GetBudgetsAsync_404_ThrowsYnabNotFoundException()
	{
		// Arrange
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.NotFound, "{}"));

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabNotFoundException>();
	}

	[Fact]
	public async Task GetBudgetsAsync_429_ThrowsHttpRequestException()
	{
		// Arrange
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.TooManyRequests, "{}"));

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<HttpRequestException>();
	}

	[Fact]
	public async Task GetBudgetsAsync_CachesResult_SecondCallDoesNotHitHttp()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				budgets = new[]
				{
					new { id = "budget-1", name = "My Budget" },
				}
			}
		});

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

		IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
		YnabApiClient client = CreateClient(handlerMock.Object, cache: cache);

		// Act
		var first = await client.GetBudgetsAsync(CancellationToken.None);
		var second = await client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		first.Should().BeSameAs(second);
		handlerMock.Protected()
			.Verify("SendAsync", Times.Once(),
				ItExpr.IsAny<HttpRequestMessage>(),
				ItExpr.IsAny<CancellationToken>());
	}

	[Fact]
	public async Task GetCategoriesAsync_FiltersInternalMasterCategoryGroup()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				category_groups = new object[]
				{
					new
					{
						id = "group-internal", name = "Internal Master Category", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-tbb", category_group_id = "group-internal", category_group_name = "Internal Master Category", name = "To be Budgeted", hidden = false, deleted = false },
						}
					},
					new
					{
						id = "group-normal", name = "Everyday Expenses", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-groceries", category_group_id = "group-normal", category_group_name = "Everyday Expenses", name = "Groceries", hidden = false, deleted = false },
						}
					},
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		// Act
		var categories = await client.GetCategoriesAsync("budget-1", CancellationToken.None);

		// Assert
		categories.Should().HaveCount(1);
		categories[0].Name.Should().Be("Groceries");
		categories[0].CategoryGroupName.Should().Be("Everyday Expenses");
	}

	[Fact]
	public async Task GetCategoriesAsync_FiltersCreditCardPaymentsGroup()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				category_groups = new object[]
				{
					new
					{
						id = "group-cc", name = "Credit Card Payments", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-visa", category_group_id = "group-cc", category_group_name = "Credit Card Payments", name = "Visa", hidden = false, deleted = false },
						}
					},
					new
					{
						id = "group-normal", name = "Monthly Bills", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-rent", category_group_id = "group-normal", category_group_name = "Monthly Bills", name = "Rent", hidden = false, deleted = false },
						}
					},
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		// Act
		var categories = await client.GetCategoriesAsync("budget-1", CancellationToken.None);

		// Assert
		categories.Should().HaveCount(1);
		categories[0].Name.Should().Be("Rent");
	}

	[Fact]
	public async Task GetCategoriesAsync_FiltersDeletedGroupsAndInternalGroups()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				category_groups = new object[]
				{
					new
					{
						id = "group-deleted", name = "Old Group", hidden = false, deleted = true,
						categories = new[]
						{
							new { id = "cat-old", category_group_id = "group-deleted", category_group_name = "Old Group", name = "Old Category", hidden = false, deleted = false },
						}
					},
					new
					{
						id = "group-internal", name = "Internal Master Category", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-tbb", category_group_id = "group-internal", category_group_name = "Internal Master Category", name = "To be Budgeted", hidden = false, deleted = false },
						}
					},
					new
					{
						id = "group-cc", name = "Credit Card Payments", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-visa", category_group_id = "group-cc", category_group_name = "Credit Card Payments", name = "Visa", hidden = false, deleted = false },
						}
					},
					new
					{
						id = "group-normal", name = "Fun Money", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-hobbies", category_group_id = "group-normal", category_group_name = "Fun Money", name = "Hobbies", hidden = false, deleted = false },
						}
					},
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		// Act
		var categories = await client.GetCategoriesAsync("budget-1", CancellationToken.None);

		// Assert
		categories.Should().HaveCount(1);
		categories[0].Name.Should().Be("Hobbies");
		categories[0].CategoryGroupName.Should().Be("Fun Money");
	}

	[Fact]
	public async Task GetCategoriesAsync_ReturnsNormalCategories_ExcludingDeletedCategories()
	{
		// Arrange
		string json = JsonSerializer.Serialize(new
		{
			data = new
			{
				category_groups = new object[]
				{
					new
					{
						id = "group-1", name = "Everyday Expenses", hidden = false, deleted = false,
						categories = new[]
						{
							new { id = "cat-1", category_group_id = "group-1", category_group_name = "Everyday Expenses", name = "Groceries", hidden = false, deleted = false },
							new { id = "cat-2", category_group_id = "group-1", category_group_name = "Everyday Expenses", name = "Deleted Cat", hidden = false, deleted = true },
						}
					},
				}
			}
		});

		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, json));

		// Act
		var categories = await client.GetCategoriesAsync("budget-1", CancellationToken.None);

		// Assert
		categories.Should().HaveCount(1);
		categories[0].Name.Should().Be("Groceries");
	}

	[Fact]
	public void IsConfigured_ReturnsFalse_WhenPatIsMissing()
	{
		// Arrange
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"), pat: null);

		// Act & Assert
		client.IsConfigured.Should().BeFalse();
	}

	[Fact]
	public void IsConfigured_ReturnsTrue_WhenPatIsPresent()
	{
		// Arrange
		YnabApiClient client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"), pat: "test-pat");

		// Act & Assert
		client.IsConfigured.Should().BeTrue();
	}
}
