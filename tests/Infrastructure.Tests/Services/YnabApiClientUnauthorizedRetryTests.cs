using System.Net;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Ynab;
using Infrastructure.Ynab;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests.Services;

/// <summary>
/// What the YNAB API client does when YNAB answers 401: an OAuth access token is renewed once
/// and the request repeated once; a personal access token's 401 stands.
/// </summary>
public class YnabApiClientUnauthorizedRetryTests
{
	private static readonly YnabBearerCredential Rejected = new("access-token-one-a1", "oauth-11111111111111111111111111111111");
	private static readonly YnabBearerCredential Renewed = new("access-token-two-a2", "oauth-11111111111111111111111111111111");

	private static readonly string BudgetsJson = JsonSerializer.Serialize(new
	{
		data = new { budgets = new[] { new { id = "budget-1", name = "My Budget" } } },
	});

	private readonly Mock<IYnabCredentialSource> _credentialSource = new();

	private static YnabApiClient CreateClient(HttpMessageHandler handler, IYnabCredentialSource credentialSource)
	{
		HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://api.ynab.com/v1/") };
		return new YnabApiClient(
			httpClient,
			new MemoryCache(new MemoryCacheOptions()),
			credentialSource,
			Mock.Of<IYnabRateLimitTracker>(),
			new YnabResponseContext(),
			NullLogger<YnabApiClient>.Instance);
	}

	private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	/// <summary>Answers 401 to the rejected token and <paramref name="accepted"/> to any other.</summary>
	private static StubHttpMessageHandler RejectingFirstToken(Func<HttpResponseMessage> accepted) =>
		new(request => request.Headers.Authorization?.Parameter == Rejected.Token
			? Json(HttpStatusCode.Unauthorized, """{"error":{"id":"401","name":"unauthorized"}}""")
			: accepted());

	private void SetupOAuthCredential() =>
		_credentialSource.Setup(s => s.GetCredentialAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Rejected);

	[Fact]
	public async Task OAuthToken401_RenewalSucceeds_RepeatsTheRequestOnceWithTheNewTokenAndReturnsItsResult()
	{
		// Arrange
		SetupOAuthCredential();
		_credentialSource.Setup(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>())).ReturnsAsync(Renewed);
		StubHttpMessageHandler handler = RejectingFirstToken(() => Json(HttpStatusCode.OK, BudgetsJson));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);

		// Act
		List<YnabBudget> budgets = await client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		budgets.Should().Equal(new YnabBudget("budget-1", "My Budget"));
		handler.Requests.Select(r => r.Authorization).Should().Equal(
			"Bearer access-token-one-a1", "Bearer access-token-two-a2");
		handler.Requests.Select(r => r.Uri).Should().AllBeEquivalentTo(new Uri("https://api.ynab.com/v1/budgets"));
		_credentialSource.Verify(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task OAuthToken401_RenewalRefused_ThrowsYnabAuthExceptionWithoutRepeatingTheRequest()
	{
		// Arrange
		SetupOAuthCredential();
		_credentialSource.Setup(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new YnabAuthException("YNAB no longer accepts the stored connection. Reconnect YNAB."));
		StubHttpMessageHandler handler = RejectingFirstToken(() => Json(HttpStatusCode.OK, BudgetsJson));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<YnabAuthException>()).WithMessage("*Reconnect YNAB*");
		handler.Requests.Should().ContainSingle();
	}

	[Fact]
	public async Task OAuthToken401_RenewalCannotReachYnab_FailsAsATemporaryFailureWithoutRepeatingTheRequest()
	{
		// Arrange
		SetupOAuthCredential();
		_credentialSource.Setup(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new YnabTokenRenewalUnavailableException("YNAB could not be reached to renew the connection."));
		StubHttpMessageHandler handler = RejectingFirstToken(() => Json(HttpStatusCode.OK, BudgetsJson));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<HttpRequestException>();
		handler.Requests.Should().ContainSingle();
	}

	[Fact]
	public async Task OAuthToken401_RenewedTokenAlsoRejected_ThrowsAfterExactlyOneRetry()
	{
		// Arrange
		SetupOAuthCredential();
		_credentialSource.Setup(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>())).ReturnsAsync(Renewed);
		StubHttpMessageHandler handler = new(_ => Json(HttpStatusCode.Unauthorized, "{}"));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
		handler.Requests.Should().HaveCount(2);
		_credentialSource.Verify(
			s => s.RenewAfterRejectionAsync(It.IsAny<YnabBearerCredential>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task PatToken401_IsNotRetried()
	{
		// Arrange — the real credential source, with only YNAB_PAT set.
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["YNAB_PAT"] = "personal-access-token-p1" })
			.Build();
		StubHttpMessageHandler handler = new(_ => Json(HttpStatusCode.Unauthorized, "{}"));
		YnabApiClient client = CreateClient(handler, YnabCredentialSourceFactory.PatOnly(configuration));

		// Act
		Func<Task> act = () => client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
		handler.Requests.Should().ContainSingle()
			.Which.Authorization.Should().Be("Bearer personal-access-token-p1");
	}

	[Fact]
	public async Task OAuthToken401_OnCreateTransaction_ResendsTheSameBodyOnTheRetry()
	{
		// Arrange
		SetupOAuthCredential();
		_credentialSource.Setup(s => s.RenewAfterRejectionAsync(Rejected, It.IsAny<CancellationToken>())).ReturnsAsync(Renewed);
		StubHttpMessageHandler handler = RejectingFirstToken(() => Json(
			HttpStatusCode.Created,
			"""{"data":{"transaction_ids":["ynab-transaction-1"],"transaction":{"id":"ynab-transaction-1"}}}"""));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);
		YnabCreateTransactionRequest request = new(
			"account-1", new DateOnly(2026, 11, 19), -12340, "weekly shop", "Grocer", "category-1", false,
			ImportId: "operation-1");

		// Act
		YnabCreateTransactionResponse response = await client.CreateTransactionAsync("budget-1", request, CancellationToken.None);

		// Assert
		response.TransactionId.Should().Be("ynab-transaction-1");
		handler.Requests.Should().HaveCount(2);
		CapturedRequest first = handler.Requests[0];
		CapturedRequest retry = handler.Requests[1];
		first.Authorization.Should().Be("Bearer access-token-one-a1");
		retry.Authorization.Should().Be("Bearer access-token-two-a2");
		retry.Method.Should().Be(HttpMethod.Post);
		retry.Uri.Should().Be(new Uri("https://api.ynab.com/v1/budgets/budget-1/transactions"));
		retry.Body.Should().Be(first.Body);

		using JsonDocument sent = JsonDocument.Parse(retry.Body!);
		JsonElement transaction = sent.RootElement.GetProperty("transaction");
		transaction.GetProperty("account_id").GetString().Should().Be("account-1");
		transaction.GetProperty("amount").GetInt64().Should().Be(-12340);
		transaction.GetProperty("date").GetString().Should().Be("2026-11-19");
		transaction.GetProperty("payee_name").GetString().Should().Be("Grocer");
		transaction.GetProperty("memo").GetString().Should().Be("[Receipts push:operation-1] weekly shop");
	}

	[Fact]
	public async Task Budgets_CachedUnderOneConnection_AreNotServedAfterReconnecting()
	{
		// Arrange — two successive connections differ only in their cache identity.
		YnabBearerCredential firstConnection = new("access-token-one-a1", "oauth-11111111111111111111111111111111");
		YnabBearerCredential secondConnection = new("access-token-three-a3", "oauth-22222222222222222222222222222222");
		YnabBearerCredential current = firstConnection;
		_credentialSource.Setup(s => s.GetCredentialAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => current);
		StubHttpMessageHandler handler = new(request => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
		{
			data = new
			{
				budgets = new[]
				{
					request.Headers.Authorization!.Parameter == firstConnection.Token
						? new { id = "budget-first", name = "First account" }
						: new { id = "budget-second", name = "Second account" },
				},
			},
		})));
		YnabApiClient client = CreateClient(handler, _credentialSource.Object);

		// Act
		List<YnabBudget> underFirst = await client.GetBudgetsAsync(CancellationToken.None);
		List<YnabBudget> underFirstAgain = await client.GetBudgetsAsync(CancellationToken.None);
		current = secondConnection;
		List<YnabBudget> underSecond = await client.GetBudgetsAsync(CancellationToken.None);

		// Assert
		underFirst.Should().Equal(new YnabBudget("budget-first", "First account"));
		underFirstAgain.Should().Equal(new YnabBudget("budget-first", "First account"));
		underSecond.Should().Equal(new YnabBudget("budget-second", "Second account"));
		handler.Requests.Should().HaveCount(2, "the first connection's budgets are cached, the second's are fetched");
	}
}
