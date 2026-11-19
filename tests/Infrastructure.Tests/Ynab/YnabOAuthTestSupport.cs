using System.Collections.Concurrent;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Repositories;
using Infrastructure.Tests.Repositories;
using Infrastructure.Ynab;
using Infrastructure.Ynab.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Infrastructure.Tests.Ynab;

/// <summary>
/// Builds the credential source for tests that only care about the <c>YNAB_PAT</c> path: no
/// stored connection, no OAuth client settings.
/// </summary>
internal static class YnabCredentialSourceFactory
{
	public static YnabCredentialSource PatOnly(IConfiguration configuration) =>
		new(
			configuration,
			new YnabOAuthSettingsProvider(configuration),
			Mock.Of<IYnabOAuthConnectionRepository>(),
			new YnabOAuthTokenProtector(new EphemeralDataProtectionProvider()),
			Mock.Of<IYnabOAuthClient>(),
			TimeProvider.System,
			NullLogger<YnabCredentialSource>.Instance);
}

internal sealed record CapturedLog(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>Records every log entry so a test can assert what was, and was not, written.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<CapturedLog> _entries = new();

	public IReadOnlyList<CapturedLog> Entries => [.. _entries];

	/// <summary>Everything a log sink could have received: rendered messages and exception text.</summary>
	public string AllText => string.Join(Environment.NewLine, _entries.Select(e => $"{e.Level} {e.EventId} {e.Message} {e.Exception}"));

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
		_entries.Enqueue(new CapturedLog(logLevel, eventId, formatter(state, exception), exception));
}

internal sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? Body, string? ContentType, string? Authorization);

/// <summary>Answers each request from a function and keeps what was sent, body included.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
	private readonly ConcurrentQueue<CapturedRequest> _requests = new();

	public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
		: this((request, _) => Task.FromResult(respond(request)))
	{
	}

	public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		_requests.Enqueue(new CapturedRequest(
			request.Method,
			request.RequestUri,
			body,
			request.Content?.Headers.ContentType?.MediaType,
			request.Headers.Authorization?.ToString()));
		return await respond(request, cancellationToken);
	}
}

/// <summary>Hands out clients over one handler and remembers which named clients were asked for.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
{
	private readonly ConcurrentQueue<string> _names = new();

	public IReadOnlyList<string> RequestedNames => [.. _names];

	public HttpClient CreateClient(string name)
	{
		_names.Enqueue(name);
		return new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
	}
}

/// <summary>
/// The real repository over an in-memory database, with a count of reads and two ways to make
/// the token commit fail. Row assertions go through <see cref="ReadRowAsync"/>, which is not counted.
/// </summary>
internal sealed class SpyConnectionRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabOAuthConnectionRepository
{
	private readonly YnabOAuthConnectionRepository _inner = new(contextFactory);
	private int _getCalls;

	public int GetCalls => _getCalls;

	/// <summary>Runs just before the renewed tokens are committed. May throw, or change the row.</summary>
	public Func<Task>? BeforeReplaceTokens { get; set; }

	public YnabOAuthConnectionRepository Inner => _inner;

	public Task<YnabOAuthConnectionEntity?> ReadRowAsync() => _inner.GetAsync(CancellationToken.None);

	public Task<YnabOAuthConnectionEntity?> GetAsync(CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref _getCalls);
		return _inner.GetAsync(cancellationToken);
	}

	public Task<YnabOAuthConnectionEntity> SaveConnectedAsync(
		string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, string? connectedByUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
		_inner.SaveConnectedAsync(protectedTokens, accessTokenExpiresAtUtc, connectedByUserId, now, cancellationToken);

	public async Task<bool> TryReplaceTokensAsync(
		Guid expectedStamp, string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
	{
		if (BeforeReplaceTokens is not null)
		{
			await BeforeReplaceTokens();
		}

		return await _inner.TryReplaceTokensAsync(expectedStamp, protectedTokens, accessTokenExpiresAtUtc, now, cancellationToken);
	}

	public Task<bool> MarkReconnectRequiredAsync(Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken) =>
		_inner.MarkReconnectRequiredAsync(connectionId, now, cancellationToken);

	/// <summary>Runs before the row is deleted. Throwing leaves the row in place.</summary>
	public Func<Task>? BeforeDelete { get; set; }

	/// <summary>Runs after the row is deleted. Throwing models a delete whose answer was lost.</summary>
	public Func<Task>? AfterDelete { get; set; }

	public async Task<bool> DeleteAsync(CancellationToken cancellationToken)
	{
		if (BeforeDelete is not null)
		{
			await BeforeDelete();
		}

		bool deleted = await _inner.DeleteAsync(cancellationToken);
		if (AfterDelete is not null)
		{
			await AfterDelete();
		}

		return deleted;
	}
}

internal static class YnabOAuthConnectionStoreTestExtensions
{
	/// <summary>Removes the stored connection with no remote step, as a test's arrangement.</summary>
	public static Task RemoveWithoutRevokingAsync(this IYnabOAuthConnectionStore store) =>
		store.RemoveAsync<object?>((_, _) => Task.FromResult<object?>(null), CancellationToken.None);
}

/// <summary>
/// A credential source wired to real collaborators (repository on an in-memory database, Data
/// Protection with throwaway keys, fake clock) and a mocked YNAB OAuth client.
/// </summary>
internal sealed class YnabCredentialSourceHarness : IDisposable
{
	public const string ClientId = "client-id-1";
	public const string ClientSecret = "client-secret-9f3c";
	public const string RedirectUri = "https://receipts.example/settings/ynab/callback";

	public static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
	public static readonly YnabOAuthSettings Settings = new(ClientId, ClientSecret, RedirectUri);

	private readonly Dictionary<string, string?> _values = [];

	public YnabCredentialSourceHarness(string? pat = null, bool oauthSettings = true)
	{
		if (pat is not null)
		{
			_values["YNAB_PAT"] = pat;
		}

		if (oauthSettings)
		{
			_values["YNAB_OAUTH_CLIENT_ID"] = ClientId;
			_values["YNAB_OAUTH_CLIENT_SECRET"] = ClientSecret;
			_values["YNAB_OAUTH_REDIRECT_URI"] = RedirectUri;
		}

		Configuration = new ConfigurationBuilder().AddInMemoryCollection(_values).Build();
		SettingsProvider = new YnabOAuthSettingsProvider(Configuration);
		Repository = new SpyConnectionRepository(DbContextHelpers.CreateInMemoryContextFactory());
		Protector = new YnabOAuthTokenProtector(new EphemeralDataProtectionProvider());
		Source = new YnabCredentialSource(
			Configuration, SettingsProvider, Repository, Protector, OAuthClient.Object, Time, Logger);
	}

	public IConfigurationRoot Configuration { get; }
	public YnabOAuthSettingsProvider SettingsProvider { get; }
	public FakeTimeProvider Time { get; } = new(Start);
	public SpyConnectionRepository Repository { get; }
	public YnabOAuthTokenProtector Protector { get; }
	public Mock<IYnabOAuthClient> OAuthClient { get; } = new(MockBehavior.Strict);
	public CapturingLogger<YnabCredentialSource> Logger { get; } = new();
	public YnabCredentialSource Source { get; }

	/// <summary>Writes a connected row directly, as an earlier authorization would have left it.</summary>
	public Task<YnabOAuthConnectionEntity> SeedConnectedAsync(
		string accessToken, string refreshToken, DateTimeOffset expiresAtUtc, string? userId = "admin-1") =>
		Repository.Inner.SaveConnectedAsync(
			Protector.Protect(accessToken, refreshToken), expiresAtUtc, userId, Time.GetUtcNow(), CancellationToken.None);

	public async Task<YnabOAuthConnectionEntity> SeedReconnectRequiredAsync()
	{
		YnabOAuthConnectionEntity row = await SeedConnectedAsync("discarded-access", "discarded-refresh", Start.AddHours(2));
		await Repository.Inner.MarkReconnectRequiredAsync(row.ConnectionId, Time.GetUtcNow(), CancellationToken.None);
		return row;
	}

	/// <summary>The stored pair as this deployment can read it, or null when it cannot.</summary>
	public async Task<(string AccessToken, string RefreshToken)?> ReadStoredTokensAsync()
	{
		YnabOAuthConnectionEntity? row = await Repository.ReadRowAsync();
		return Protector.TryUnprotect(row?.ProtectedTokens, out string? accessToken, out string? refreshToken)
			? (accessToken, refreshToken)
			: null;
	}

	public void Dispose() => Source.Dispose();
}
