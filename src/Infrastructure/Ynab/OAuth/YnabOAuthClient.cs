using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ynab.OAuth;

/// <param name="AccessToken">Never log or return it.</param>
/// <param name="RefreshToken">Never log or return it.</param>
public sealed record YnabOAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAtUtc)
{
	// A record's generated ToString would print both tokens.
	public override string ToString() => $"{nameof(YnabOAuthTokens)} {{ AccessTokenExpiresAtUtc = {AccessTokenExpiresAtUtc:O} }}";
}

public enum YnabTokenOutcome
{
	Success,
	/// <summary>YNAB refused the code or refresh token itself (<c>invalid_grant</c>).</summary>
	InvalidGrant,
	/// <summary>YNAB refused the request for another reason, such as wrong client credentials.</summary>
	Rejected,
	/// <summary>No usable answer: transport failure, timeout, 5xx or throttling.</summary>
	Unavailable,
}

/// <param name="Error">YNAB's OAuth error code, when it gave one. Safe to log.</param>
public sealed record YnabTokenResult(YnabTokenOutcome Outcome, YnabOAuthTokens? Tokens = null, string? Error = null);

public interface IYnabOAuthClient
{
	string BuildAuthorizationUrl(YnabOAuthSettings settings, string state, string codeChallenge);
	Task<YnabTokenResult> ExchangeCodeAsync(YnabOAuthSettings settings, string code, string codeVerifier, CancellationToken cancellationToken);
	Task<YnabTokenResult> RefreshAsync(YnabOAuthSettings settings, string refreshToken, CancellationToken cancellationToken);

	/// <summary>Asks YNAB to end the grant the token belongs to. True when YNAB accepted the request.</summary>
	Task<bool> RevokeAsync(YnabOAuthSettings settings, string token, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to YNAB's OAuth endpoints on app.ynab.com. Deliberately separate from the YNAB API
/// client: these calls are not API requests, so they share neither its retry policy nor its
/// rate-limit count. Nothing here logs a request or response body.
/// </summary>
public class YnabOAuthClient(
	IHttpClientFactory httpClientFactory,
	YnabClientOptions options,
	TimeProvider timeProvider,
	ILogger<YnabOAuthClient> logger) : IYnabOAuthClient
{
	public const string HttpClientName = "ynab-oauth";

	public string BuildAuthorizationUrl(YnabOAuthSettings settings, string state, string codeChallenge)
	{
		// No scope: the default grant allows writes, which pushing transactions needs.
		(string Name, string Value)[] query =
		[
			("client_id", settings.ClientId),
			("redirect_uri", settings.RedirectUri),
			("response_type", "code"),
			("state", state),
			("code_challenge", codeChallenge),
			("code_challenge_method", "S256"),
		];

		string queryString = string.Join("&", query.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
		return $"{options.OAuthBaseUrl.TrimEnd('/')}/oauth/authorize?{queryString}";
	}

	public Task<YnabTokenResult> ExchangeCodeAsync(YnabOAuthSettings settings, string code, string codeVerifier, CancellationToken cancellationToken) =>
		RequestTokensAsync(
			new Dictionary<string, string>
			{
				["grant_type"] = "authorization_code",
				["client_id"] = settings.ClientId,
				["client_secret"] = settings.ClientSecret,
				["redirect_uri"] = settings.RedirectUri,
				["code"] = code,
				["code_verifier"] = codeVerifier,
			},
			cancellationToken);

	public Task<YnabTokenResult> RefreshAsync(YnabOAuthSettings settings, string refreshToken, CancellationToken cancellationToken) =>
		RequestTokensAsync(
			new Dictionary<string, string>
			{
				["grant_type"] = "refresh_token",
				["client_id"] = settings.ClientId,
				["client_secret"] = settings.ClientSecret,
				["refresh_token"] = refreshToken,
			},
			cancellationToken);

	public async Task<bool> RevokeAsync(YnabOAuthSettings settings, string token, CancellationToken cancellationToken)
	{
		try
		{
			using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
			using FormUrlEncodedContent content = new(new Dictionary<string, string>
			{
				["client_id"] = settings.ClientId,
				["client_secret"] = settings.ClientSecret,
				["token"] = token,
			});
			using HttpResponseMessage response = await client.PostAsync("oauth/revoke", content, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning("YNAB refused the revocation request with status {StatusCode}", (int)response.StatusCode);
			}

			return response.IsSuccessStatusCode;
		}
		catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
		{
			logger.LogWarning("YNAB could not be reached to revoke the connection ({Failure})", ex.GetType().Name);
			return false;
		}
	}

	private async Task<YnabTokenResult> RequestTokensAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
	{
		// Expiry is measured from before the request, so it is never later than YNAB's own.
		DateTimeOffset requestedAt = timeProvider.GetUtcNow();
		string grantType = form["grant_type"];
		try
		{
			using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
			using FormUrlEncodedContent content = new(form);
			using HttpResponseMessage response = await client.PostAsync("oauth/token", content, cancellationToken);

			if (response.IsSuccessStatusCode)
			{
				TokenResponse? body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
				if (body is null
					|| string.IsNullOrWhiteSpace(body.AccessToken)
					|| string.IsNullOrWhiteSpace(body.RefreshToken)
					|| body.ExpiresIn <= 0)
				{
					logger.LogError("YNAB answered a {GrantType} token request without a usable token pair", grantType);
					return new YnabTokenResult(YnabTokenOutcome.Unavailable);
				}

				return new YnabTokenResult(
					YnabTokenOutcome.Success,
					new YnabOAuthTokens(body.AccessToken, body.RefreshToken, requestedAt.AddSeconds(body.ExpiresIn)));
			}

			int status = (int)response.StatusCode;
			bool refused = status is >= 400 and < 500
				&& response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
			if (!refused)
			{
				logger.LogWarning("YNAB token endpoint answered a {GrantType} request with status {StatusCode}", grantType, status);
				return new YnabTokenResult(YnabTokenOutcome.Unavailable);
			}

			string? error = await ReadErrorCodeAsync(response, cancellationToken);
			logger.LogWarning(
				"YNAB refused a {GrantType} token request: status {StatusCode}, error {Error}", grantType, status, error ?? "(none)");
			return new YnabTokenResult(
				error == "invalid_grant" ? YnabTokenOutcome.InvalidGrant : YnabTokenOutcome.Rejected,
				Error: error);
		}
		catch (Exception ex) when (IsTransportFailure(ex, cancellationToken) || ex is JsonException)
		{
			logger.LogWarning("YNAB token endpoint could not be used for a {GrantType} request ({Failure})", grantType, ex.GetType().Name);
			return new YnabTokenResult(YnabTokenOutcome.Unavailable);
		}
	}

	// Only the OAuth error code is read. It is one of a small fixed vocabulary, so it is kept
	// short and plain before being logged.
	private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			ErrorResponse? body = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken);
			string? error = body?.Error;
			return error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
				? error
				: null;
		}
		catch (Exception ex) when (ex is JsonException or NotSupportedException)
		{
			return null;
		}
	}

	// A timeout surfaces as a cancellation the caller did not ask for.
	private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
		exception is HttpRequestException
		|| (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

	private sealed record TokenResponse(
		[property: JsonPropertyName("access_token")] string? AccessToken,
		[property: JsonPropertyName("refresh_token")] string? RefreshToken,
		[property: JsonPropertyName("expires_in")] long ExpiresIn);

	private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);
}
