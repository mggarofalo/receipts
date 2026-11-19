using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Infrastructure.Ynab;
using Infrastructure.Ynab.OAuth;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.Tests.Ynab;

public class YnabOAuthClientTests
{
	private const string ClientId = "client-id-1";
	private const string ClientSecret = "client-secret-9f3c";
	private const string RedirectUri = "https://receipts.example/settings/ynab/callback";
	private const string Code = "auth-code-7c1d";
	private const string CodeVerifier = "verifier-5e2a-0123456789-0123456789-0123456789";
	private const string StoredRefreshToken = "refresh-token-old-r0";
	private const string NewAccessToken = "access-token-new-a1";
	private const string NewRefreshToken = "refresh-token-new-r1";

	private static readonly YnabOAuthSettings Settings = new(ClientId, ClientSecret, RedirectUri);
	private static readonly DateTimeOffset RequestedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

	private static readonly string[] Secrets =
		[ClientSecret, Code, CodeVerifier, StoredRefreshToken, NewAccessToken, NewRefreshToken];

	private readonly FakeTimeProvider _time = new(RequestedAt);
	private readonly CapturingLogger<YnabOAuthClient> _logger = new();

	private (YnabOAuthClient Client, StubHttpMessageHandler Handler, StubHttpClientFactory Factory) CreateClient(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
	{
		StubHttpMessageHandler handler = new(respond);
		StubHttpClientFactory factory = new(handler, new Uri("https://app.ynab.com/"));
		return (new YnabOAuthClient(factory, new YnabClientOptions(), _time, _logger), handler, factory);
	}

	private (YnabOAuthClient Client, StubHttpMessageHandler Handler, StubHttpClientFactory Factory) CreateClient(
		HttpStatusCode status, string body) =>
		CreateClient((_, _) => Task.FromResult(Json(status, body)));

	private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static string TokenBody(string accessToken = NewAccessToken, string refreshToken = NewRefreshToken, long expiresIn = 7200) =>
		$$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":{{expiresIn}},"refresh_token":"{{refreshToken}}","created_at":1795000000}""";

	private static Dictionary<string, string?> Form(string? body)
	{
		NameValueCollection parsed = HttpUtility.ParseQueryString(body ?? string.Empty);
		return parsed.AllKeys.ToDictionary(key => key!, key => parsed[key]);
	}

	private void AssertNoSecretLogged()
	{
		string logged = _logger.AllText;
		foreach (string secret in Secrets)
		{
			logged.Should().NotContain(secret);
		}
	}

	[Fact]
	public void BuildAuthorizationUrl_CarriesClientRedirectStateAndS256ChallengeButNoScope()
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, _) = CreateClient(HttpStatusCode.OK, "{}");

		// Act
		string url = client.BuildAuthorizationUrl(Settings, "state-value-1", "challenge-value-1");

		// Assert
		Uri uri = new(url);
		uri.GetLeftPart(UriPartial.Path).Should().Be("https://app.ynab.com/oauth/authorize");
		Dictionary<string, string?> query = Form(uri.Query.TrimStart('?'));
		query.Should().BeEquivalentTo(new Dictionary<string, string?>
		{
			["client_id"] = ClientId,
			["redirect_uri"] = RedirectUri,
			["response_type"] = "code",
			["state"] = "state-value-1",
			["code_challenge"] = "challenge-value-1",
			["code_challenge_method"] = "S256",
		});
		query.Should().NotContainKey("scope", "the default grant is the one that allows writes");
		url.Should().NotContain(ClientSecret);
		handler.Requests.Should().BeEmpty();
	}

	[Fact]
	public void BuildAuthorizationUrl_EscapesValuesSoTheyRoundTrip()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient(HttpStatusCode.OK, "{}");
		YnabOAuthSettings settings = new("id with space&x=1", ClientSecret, "https://receipts.example/cb?tenant=a&b=c");

		// Act
		string url = client.BuildAuthorizationUrl(settings, "st+ate/=", "chal&lenge");

		// Assert
		Dictionary<string, string?> query = Form(new Uri(url).Query.TrimStart('?'));
		query["client_id"].Should().Be("id with space&x=1");
		query["redirect_uri"].Should().Be("https://receipts.example/cb?tenant=a&b=c");
		query["state"].Should().Be("st+ate/=");
		query["code_challenge"].Should().Be("chal&lenge");
		query.Should().HaveCount(6);
	}

	[Fact]
	public async Task ExchangeCodeAsync_PostsTheDocumentedFormToTheTokenEndpoint()
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, StubHttpClientFactory factory) =
			CreateClient(HttpStatusCode.OK, TokenBody());

		// Act
		await client.ExchangeCodeAsync(Settings, Code, CodeVerifier, CancellationToken.None);

		// Assert
		CapturedRequest request = handler.Requests.Should().ContainSingle().Subject;
		request.Method.Should().Be(HttpMethod.Post);
		request.Uri.Should().Be(new Uri("https://app.ynab.com/oauth/token"));
		request.ContentType.Should().Be("application/x-www-form-urlencoded");
		Form(request.Body).Should().BeEquivalentTo(new Dictionary<string, string?>
		{
			["grant_type"] = "authorization_code",
			["client_id"] = ClientId,
			["client_secret"] = ClientSecret,
			["redirect_uri"] = RedirectUri,
			["code"] = Code,
			["code_verifier"] = CodeVerifier,
		});
		factory.RequestedNames.Should().Equal("ynab-oauth");
	}

	[Fact]
	public async Task RefreshAsync_PostsTheDocumentedFormToTheTokenEndpoint()
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, StubHttpClientFactory factory) =
			CreateClient(HttpStatusCode.OK, TokenBody());

		// Act
		await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		CapturedRequest request = handler.Requests.Should().ContainSingle().Subject;
		request.Method.Should().Be(HttpMethod.Post);
		request.Uri.Should().Be(new Uri("https://app.ynab.com/oauth/token"));
		request.ContentType.Should().Be("application/x-www-form-urlencoded");
		Form(request.Body).Should().BeEquivalentTo(new Dictionary<string, string?>
		{
			["grant_type"] = "refresh_token",
			["client_id"] = ClientId,
			["client_secret"] = ClientSecret,
			["refresh_token"] = StoredRefreshToken,
		});
		factory.RequestedNames.Should().Equal("ynab-oauth");
	}

	[Fact]
	public async Task ExchangeCodeAsync_Success_ReturnsThePairWithExpiryMeasuredFromBeforeTheRequest()
	{
		// Arrange — the request itself takes thirty seconds.
		(YnabOAuthClient client, _, _) = CreateClient((_, _) =>
		{
			_time.Advance(TimeSpan.FromSeconds(30));
			return Task.FromResult(Json(HttpStatusCode.OK, TokenBody(expiresIn: 7200)));
		});

		// Act
		YnabTokenResult result = await client.ExchangeCodeAsync(Settings, Code, CodeVerifier, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Success);
		result.Tokens!.AccessToken.Should().Be(NewAccessToken);
		result.Tokens.RefreshToken.Should().Be(NewRefreshToken);
		result.Tokens.AccessTokenExpiresAtUtc.Should().Be(RequestedAt.AddSeconds(7200));
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RefreshAsync_Success_ReturnsThePairWithExpiryMeasuredFromBeforeTheRequest()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient((_, _) =>
		{
			_time.Advance(TimeSpan.FromSeconds(12));
			return Task.FromResult(Json(HttpStatusCode.OK, TokenBody(expiresIn: 3600)));
		});

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Success);
		result.Tokens!.AccessToken.Should().Be(NewAccessToken);
		result.Tokens.RefreshToken.Should().Be(NewRefreshToken);
		result.Tokens.AccessTokenExpiresAtUtc.Should().Be(RequestedAt.AddSeconds(3600));
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Unauthorized)]
	public async Task RefreshAsync_InvalidGrant_ReportsInvalidGrant(HttpStatusCode status)
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, _) = CreateClient(
			status, """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid, expired, revoked."}""");

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.InvalidGrant);
		result.Error.Should().Be("invalid_grant");
		result.Tokens.Should().BeNull();
		handler.Requests.Should().ContainSingle("a refusal is never retried");
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task ExchangeCodeAsync_InvalidGrant_ReportsInvalidGrant()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

		// Act
		YnabTokenResult result = await client.ExchangeCodeAsync(Settings, Code, CodeVerifier, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.InvalidGrant);
		result.Tokens.Should().BeNull();
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData(HttpStatusCode.Unauthorized, "invalid_client")]
	[InlineData(HttpStatusCode.BadRequest, "invalid_request")]
	[InlineData(HttpStatusCode.BadRequest, "unsupported_grant_type")]
	public async Task RefreshAsync_OtherRefusal_ReportsRejectedWithTheErrorCode(HttpStatusCode status, string error)
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, _) = CreateClient(status, $$"""{"error":"{{error}}"}""");

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Rejected);
		result.Error.Should().Be(error);
		result.Tokens.Should().BeNull();
		handler.Requests.Should().ContainSingle("a refusal is never retried");
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData("<html>Forbidden</html>")]
	[InlineData("")]
	[InlineData("""{"message":"no error member"}""")]
	public async Task RefreshAsync_RefusalWithoutAnErrorCode_ReportsRejectedWithNoError(string body)
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient(HttpStatusCode.Forbidden, body);

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Rejected);
		result.Error.Should().BeNull();
	}

	[Fact]
	public async Task RefreshAsync_RefusalEchoingSecretsInTheBody_LogsNoneOfThem()
	{
		// Arrange — an error value that is not a plain OAuth code is not trusted enough to log.
		(YnabOAuthClient client, _, _) = CreateClient(
			HttpStatusCode.BadRequest,
			$$"""{"error":"bad token {{StoredRefreshToken}}","error_description":"client_secret {{ClientSecret}} did not match"}""");

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Rejected);
		result.Error.Should().BeNull();
		_logger.Entries.Should().NotBeEmpty("the refusal itself is still recorded");
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	[InlineData(HttpStatusCode.RequestTimeout)]
	public async Task RefreshAsync_ServerFailureOrThrottling_ReportsUnavailable(HttpStatusCode status)
	{
		// Arrange — even a body that claims invalid_grant is not believed on these statuses.
		(YnabOAuthClient client, _, _) = CreateClient(status, """{"error":"invalid_grant"}""");

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Unavailable);
		result.Tokens.Should().BeNull();
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RefreshAsync_TransportFailure_ReportsUnavailable()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient((_, _) =>
			throw new HttpRequestException($"connection reset while sending {StoredRefreshToken}"));

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Unavailable);
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task ExchangeCodeAsync_Timeout_ReportsUnavailable()
	{
		// Arrange — HttpClient reports its own timeout as a cancellation the caller never asked for.
		(YnabOAuthClient client, _, _) = CreateClient((_, _) => throw new TaskCanceledException("The request timed out."));

		// Act
		YnabTokenResult result = await client.ExchangeCodeAsync(Settings, Code, CodeVerifier, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Unavailable);
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RefreshAsync_CallerCancels_PropagatesTheCancellation()
	{
		// Arrange — the caller giving up is not a YNAB outage.
		using CancellationTokenSource cancellation = new();
		(YnabOAuthClient client, _, _) = CreateClient(async (_, token) =>
		{
			await cancellation.CancelAsync();
			token.ThrowIfCancellationRequested();
			return Json(HttpStatusCode.OK, TokenBody());
		});

		// Act
		Func<Task> act = () => client.RefreshAsync(Settings, StoredRefreshToken, cancellation.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Theory]
	[InlineData("""{"access_token":"access-token-new-a1","expires_in":7200}""")]
	[InlineData("""{"refresh_token":"refresh-token-new-r1","expires_in":7200}""")]
	[InlineData("""{"access_token":"","refresh_token":"refresh-token-new-r1","expires_in":7200}""")]
	[InlineData("""{"access_token":"access-token-new-a1","refresh_token":"refresh-token-new-r1"}""")]
	[InlineData("""{"access_token":"access-token-new-a1","refresh_token":"refresh-token-new-r1","expires_in":0}""")]
	[InlineData("""{"access_token":"access-token-new-a1","refresh_token":"refresh-token-new-r1","expires_in":-5}""")]
	[InlineData("null")]
	[InlineData("<html>maintenance</html>")]
	public async Task RefreshAsync_OkWithoutAUsablePair_ReportsUnavailable(string body)
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient(HttpStatusCode.OK, body);

		// Act
		YnabTokenResult result = await client.RefreshAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		result.Outcome.Should().Be(YnabTokenOutcome.Unavailable);
		result.Tokens.Should().BeNull();
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RevokeAsync_YnabAnswersOk_ReturnsTrueAfterPostingTheTokenWithClientCredentials()
	{
		// Arrange
		(YnabOAuthClient client, StubHttpMessageHandler handler, StubHttpClientFactory factory) =
			CreateClient(HttpStatusCode.OK, "{}");

		// Act
		bool revoked = await client.RevokeAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		revoked.Should().BeTrue();
		CapturedRequest request = handler.Requests.Should().ContainSingle().Subject;
		request.Method.Should().Be(HttpMethod.Post);
		request.Uri.Should().Be(new Uri("https://app.ynab.com/oauth/revoke"));
		Form(request.Body).Should().BeEquivalentTo(new Dictionary<string, string?>
		{
			["client_id"] = ClientId,
			["client_secret"] = ClientSecret,
			["token"] = StoredRefreshToken,
		});
		factory.RequestedNames.Should().Equal("ynab-oauth");
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.InternalServerError)]
	public async Task RevokeAsync_YnabRefusesOrFails_ReturnsFalse(HttpStatusCode status)
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient(status, $$"""{"error":"{{StoredRefreshToken}}"}""");

		// Act
		bool revoked = await client.RevokeAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		revoked.Should().BeFalse();
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RevokeAsync_TransportFailure_ReturnsFalse()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient((_, _) =>
			throw new HttpRequestException($"connection reset while sending {StoredRefreshToken}"));

		// Act
		bool revoked = await client.RevokeAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		revoked.Should().BeFalse();
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task RevokeAsync_Timeout_ReturnsFalse()
	{
		// Arrange
		(YnabOAuthClient client, _, _) = CreateClient((_, _) => throw new TaskCanceledException("The request timed out."));

		// Act
		bool revoked = await client.RevokeAsync(Settings, StoredRefreshToken, CancellationToken.None);

		// Assert
		revoked.Should().BeFalse();
	}

	[Fact]
	public void Tokens_ToString_ContainsNeitherToken()
	{
		// Arrange
		YnabOAuthTokens tokens = new(NewAccessToken, NewRefreshToken, RequestedAt);

		// Act
		string text = tokens.ToString();

		// Assert
		text.Should().NotContain(NewAccessToken).And.NotContain(NewRefreshToken);
	}
}
