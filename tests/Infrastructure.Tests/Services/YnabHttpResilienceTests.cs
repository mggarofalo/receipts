using System.Net;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Ynab;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace Infrastructure.Tests.Services;

public class YnabHttpResilienceTests
{
	[Theory]
	[InlineData("POST", false)]
	[InlineData("POST", true)]
	[InlineData("PATCH", false)]
	[InlineData("PATCH", true)]
	[InlineData("GET", false)]
	public async Task ConfiguredYnabPipeline_RetriesOnlySafeRequests(string method, bool responseLost)
	{
		IConfiguration configuration = new ConfigurationBuilder().Build();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton(configuration);
		services.RegisterInfrastructureServices(configuration);
		CountingHandler handler = new(responseLost);
		services.AddHttpClient<IYnabApiClient, YnabApiClient>()
			.ConfigurePrimaryHttpMessageHandler(() => handler);
		using ServiceProvider provider = services.BuildServiceProvider();
		using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IYnabApiClient));
		using HttpRequestMessage request = new(new HttpMethod(method), "https://api.ynab.com/v1/budgets/test/transactions");

		if (responseLost)
		{
			Func<Task> action = () => client.SendAsync(request);
			await action.Should().ThrowAsync<HttpRequestException>();
		}
		else
		{
			using HttpResponseMessage response = await client.SendAsync(request);
			response.StatusCode.Should().Be(method == "GET" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
		}
		handler.Attempts.Should().Be(method == "GET" ? 2 : 1);
	}

	[Fact]
	public async Task CreateTransaction_OpenCircuitReportsDefiniteNonSend()
	{
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?> { ["YNAB_PAT"] = "test-pat" }).Build();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton(configuration);
		services.RegisterInfrastructureServices(configuration);
		CountingHandler handler = new(true);
		services.AddHttpClient<IYnabApiClient, YnabApiClient>()
			.ConfigurePrimaryHttpMessageHandler(() => handler);
		using ServiceProvider provider = services.BuildServiceProvider();
		using IServiceScope scope = provider.CreateScope();
		IYnabApiClient client = scope.ServiceProvider.GetRequiredService<IYnabApiClient>();
		YnabCreateTransactionRequest request = new(
			"account-1", new DateOnly(2026, 10, 1), -1000, "memo", "Store", "groceries", false,
			ImportId: "operation-1");
		for (int i = 0; i < 5; i++)
		{
			Func<Task> failure = () => client.CreateTransactionAsync("budget-1", request, CancellationToken.None);
			await failure.Should().ThrowAsync<HttpRequestException>();
		}

		Func<Task> blocked = () => client.CreateTransactionAsync("budget-1", request, CancellationToken.None);

		var exception = await blocked.Should().ThrowAsync<YnabRequestNotSentException>();
		exception.Which.Should().BeAssignableTo<IYnabDefiniteRejection>();
		exception.Which.InnerException.Should().BeAssignableTo<BrokenCircuitException>();
		handler.Attempts.Should().Be(5, "the circuit must reject the sixth create before sending it");
	}

	private sealed class CountingHandler(bool responseLost) : HttpMessageHandler
	{
		public int Attempts { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Attempts++;
			if (responseLost)
			{
				throw new HttpRequestException("response lost after remote acceptance");
			}
			HttpResponseMessage response = new(Attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
			response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
			return Task.FromResult(response);
		}
	}
}
