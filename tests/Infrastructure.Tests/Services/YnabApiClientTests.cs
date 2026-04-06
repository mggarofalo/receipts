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
