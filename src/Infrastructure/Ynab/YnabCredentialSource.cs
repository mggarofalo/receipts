using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Ynab.OAuth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ynab;

/// <summary>Owns the stored OAuth connection's lifecycle. Infrastructure-internal.</summary>
public interface IYnabOAuthConnectionStore
{
	Task StoreNewAsync(YnabOAuthTokens tokens, string? connectedByUserId, CancellationToken cancellationToken);

	/// <summary>
	/// Removes the stored connection. <paramref name="beforeRemove"/> is first given the stored
	/// refresh token (null when there is no readable connection) so the caller can revoke it at
	/// YNAB. No renewal can replace the token between that call and the removal, and the
	/// connection is removed whatever the callback returns or throws.
	/// </summary>
	Task<TResult> RemoveAsync<TResult>(
		Func<string?, CancellationToken, Task<TResult>> beforeRemove, CancellationToken cancellationToken);
}

/// <summary>
/// Decides which credential YNAB requests use: a stored OAuth connection, else the
/// <c>YNAB_PAT</c> personal access token, else none. It also keeps the OAuth access token
/// valid. See docs/ynab-connection.md.
/// </summary>
/// <remarks>
/// A singleton. Every write to the stored connection goes through here, under one gate, so the
/// in-memory snapshot can be trusted without a lifetime. Readers use the snapshot lock-free.
/// </remarks>
public sealed class YnabCredentialSource(
	IConfiguration configuration,
	YnabOAuthSettingsProvider settingsProvider,
	IYnabOAuthConnectionRepository repository,
	YnabOAuthTokenProtector protector,
	IYnabOAuthClient oauthClient,
	TimeProvider timeProvider,
	ILogger<YnabCredentialSource> logger) : IYnabCredentialSource, IYnabOAuthConnectionStore, IDisposable
{
	/// <summary>YNAB refused the stored refresh token. The only sign it may have been used elsewhere.</summary>
	public static readonly EventId ConnectionRejectedEvent = new(4101, "YnabOAuthConnectionRejected");

	/// <summary>An access token is renewed once it is this close to expiring.</summary>
	public static readonly TimeSpan RenewalSkew = TimeSpan.FromMinutes(5);

	private const string OAuthIdentityPrefix = "oauth-";

	// What is known about the stored connection without reading the database. Holds the access
	// token only; the refresh token is read and decrypted when a renewal needs it.
	private sealed record Snapshot(
		YnabOAuthConnectionStatus? Status,
		Guid ConnectionId,
		DateTimeOffset? ConnectedAtUtc,
		string? AccessToken,
		DateTimeOffset? UsableUntilUtc);

	private static readonly Snapshot NoConnection = new(null, Guid.Empty, null, null, null);

	/// <summary>After YNAB could not be reached for a renewal, callers fail fast for this long.</summary>
	public static readonly TimeSpan RenewalBackoff = TimeSpan.FromSeconds(10);

	private readonly SemaphoreSlim _gate = new(1, 1);
	private volatile Snapshot? _snapshot;

	// Read and written with the gate held. Without it, every caller queued behind an unreachable
	// YNAB would repeat the renewal in turn and wait out its timeout.
	private DateTimeOffset? _renewalUnavailableUntil;

	private string? Pat => configuration[ConfigurationVariables.YnabPat];

	public async ValueTask<YnabCredentialState> GetStateAsync(CancellationToken cancellationToken)
	{
		Snapshot snapshot = await GetSnapshotAsync(cancellationToken);
		bool oauthAvailable = settingsProvider.Current is not null;

		return snapshot.Status switch
		{
			YnabOAuthConnectionStatus.Connected =>
				new YnabCredentialState(YnabCredentialKind.OAuth, oauthAvailable, snapshot.ConnectedAtUtc),
			// No fallback to the PAT here: a refused connection must stay visible.
			YnabOAuthConnectionStatus.ReconnectRequired =>
				new YnabCredentialState(YnabCredentialKind.OAuthReconnectRequired, oauthAvailable),
			_ => new YnabCredentialState(
				string.IsNullOrWhiteSpace(Pat) ? YnabCredentialKind.None : YnabCredentialKind.PersonalAccessToken,
				oauthAvailable),
		};
	}

	public async ValueTask<YnabBearerCredential?> GetCredentialAsync(CancellationToken cancellationToken)
	{
		Snapshot snapshot = await GetSnapshotAsync(cancellationToken);
		switch (snapshot.Status)
		{
			case null:
				return PatCredential();
			case YnabOAuthConnectionStatus.ReconnectRequired:
				return null;
		}

		if (snapshot.AccessToken is not null && timeProvider.GetUtcNow() < snapshot.UsableUntilUtc)
		{
			return OAuthCredential(snapshot.AccessToken, snapshot.ConnectionId);
		}

		return await RenewAsync(rejectedAccessToken: null, cancellationToken);
	}

	public async ValueTask<YnabBearerCredential?> RenewAfterRejectionAsync(YnabBearerCredential rejected, CancellationToken cancellationToken)
	{
		// A personal access token cannot be renewed; its 401 stands.
		if (!rejected.CacheIdentity.StartsWith(OAuthIdentityPrefix, StringComparison.Ordinal))
		{
			return null;
		}

		return await RenewAsync(rejected.Token, cancellationToken);
	}

	public async Task StoreNewAsync(YnabOAuthTokens tokens, string? connectedByUserId, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			YnabOAuthConnectionEntity row = await repository.SaveConnectedAsync(
				protector.Protect(tokens.AccessToken, tokens.RefreshToken),
				tokens.AccessTokenExpiresAtUtc,
				connectedByUserId,
				timeProvider.GetUtcNow(),
				cancellationToken);
			_snapshot = ConnectedSnapshot(row, tokens.AccessToken, tokens.AccessTokenExpiresAtUtc);
			_renewalUnavailableUntil = null;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<TResult> RemoveAsync<TResult>(
		Func<string?, CancellationToken, Task<TResult>> beforeRemove, CancellationToken cancellationToken)
	{
		// The gate is held from reading the token to deleting the row, so a renewal cannot rotate
		// the token in between and leave the caller revoking a retired one.
		await _gate.WaitAsync(cancellationToken);
		try
		{
			YnabOAuthConnectionEntity? row = await repository.GetAsync(cancellationToken);
			string? refreshToken = row is { Status: YnabOAuthConnectionStatus.Connected }
				&& protector.TryUnprotect(row.ProtectedTokens, out _, out string? stored)
					? stored
					: null;

			try
			{
				return await beforeRemove(refreshToken, cancellationToken);
			}
			finally
			{
				// Unknown until the delete is known to have happened. Not cancellable: once the
				// remote step has run, the local tokens must go.
				_snapshot = null;
				_renewalUnavailableUntil = null;
				await repository.DeleteAsync(CancellationToken.None);
				_snapshot = NoConnection;
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	public void Dispose() => _gate.Dispose();

	private async ValueTask<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
	{
		Snapshot? snapshot = _snapshot;
		if (snapshot is not null)
		{
			return snapshot;
		}

		await _gate.WaitAsync(cancellationToken);
		try
		{
			return _snapshot ??= await LoadSnapshotAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	// Call with the gate held.
	private async Task<Snapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
	{
		YnabOAuthConnectionEntity? row = await repository.GetAsync(cancellationToken);
		if (row is null)
		{
			return NoConnection;
		}

		if (row.Status != YnabOAuthConnectionStatus.Connected)
		{
			return ReconnectSnapshot(row);
		}

		if (!protector.TryUnprotect(row.ProtectedTokens, out string? accessToken, out _))
		{
			return await DiscardUnreadableAsync(row, cancellationToken);
		}

		return ConnectedSnapshot(row, accessToken, row.AccessTokenExpiresAtUtc);
	}

	// Renews the access token, or returns the stored one if another caller already did. With
	// the gate held, at most one renewal is ever in flight. The new pair is committed before it
	// is returned: YNAB keeps the previous pair valid until the new access token is first used,
	// so a failure anywhere before the commit leaves a stored pair that still works.
	private async Task<YnabBearerCredential?> RenewAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			YnabOAuthConnectionEntity? row = await repository.GetAsync(cancellationToken);
			if (row is null)
			{
				_snapshot = NoConnection;
				return PatCredential();
			}

			if (row.Status != YnabOAuthConnectionStatus.Connected)
			{
				_snapshot = ReconnectSnapshot(row);
				return null;
			}

			if (!protector.TryUnprotect(row.ProtectedTokens, out string? accessToken, out string? refreshToken))
			{
				_snapshot = await DiscardUnreadableAsync(row, cancellationToken);
				return null;
			}

			DateTimeOffset now = timeProvider.GetUtcNow();
			bool fresh = row.AccessTokenExpiresAtUtc is { } expiresAt && now < expiresAt - RenewalSkew;
			bool isRejectedToken = rejectedAccessToken is not null
				&& string.Equals(accessToken, rejectedAccessToken, StringComparison.Ordinal);
			if (fresh && !isRejectedToken)
			{
				_snapshot = ConnectedSnapshot(row, accessToken, row.AccessTokenExpiresAtUtc);
				return OAuthCredential(accessToken, row.ConnectionId);
			}

			if (_renewalUnavailableUntil is { } unavailableUntil && now < unavailableUntil)
			{
				throw new YnabTokenRenewalUnavailableException("YNAB could not be reached to renew the connection, so the request was not sent.");
			}

			YnabOAuthSettings? settings = settingsProvider.Current;
			if (settings is null)
			{
				logger.LogError("The YNAB connection cannot be renewed because the YNAB OAuth client settings are missing");
				throw new YnabAuthException("The YNAB connection cannot be renewed: the YNAB OAuth client settings are missing.");
			}

			YnabTokenResult result = await oauthClient.RefreshAsync(settings, refreshToken, cancellationToken);
			_renewalUnavailableUntil = result.Outcome == YnabTokenOutcome.Unavailable
				? timeProvider.GetUtcNow() + RenewalBackoff
				: null;
			switch (result.Outcome)
			{
				case YnabTokenOutcome.Success:
					YnabOAuthTokens tokens = result.Tokens!;
					bool saved = await repository.TryReplaceTokensAsync(
						row.ConcurrencyStamp,
						protector.Protect(tokens.AccessToken, tokens.RefreshToken),
						tokens.AccessTokenExpiresAtUtc,
						now,
						cancellationToken);
					if (!saved)
					{
						// The row changed underneath the renewal. An unsaved token is never handed out.
						_snapshot = null;
						logger.LogWarning("The YNAB connection changed while it was being renewed; the renewed tokens were discarded");
						throw new YnabTokenRenewalUnavailableException("The YNAB connection changed while it was being renewed. Try again.");
					}

					_snapshot = ConnectedSnapshot(row, tokens.AccessToken, tokens.AccessTokenExpiresAtUtc);
					return OAuthCredential(tokens.AccessToken, row.ConnectionId);

				case YnabTokenOutcome.InvalidGrant:
					await repository.MarkReconnectRequiredAsync(row.ConnectionId, now, cancellationToken);
					_snapshot = null;
					logger.LogWarning(
						ConnectionRejectedEvent,
						"YNAB refused the stored refresh token for connection {ConnectionId}. The stored tokens were discarded and an Admin must reconnect. If access was not removed in YNAB, the token may have been used elsewhere.",
						row.ConnectionId);
					throw new YnabAuthException("YNAB no longer accepts the stored connection. Reconnect YNAB.");

				case YnabTokenOutcome.Rejected:
					logger.LogError(
						"YNAB refused this deployment's OAuth client settings while renewing the connection (error {Error}). The stored connection was kept.",
						result.Error ?? "(none)");
					throw new YnabAuthException("YNAB refused this deployment's OAuth client settings. Check the YNAB OAuth client id and secret.");

				default:
					throw new YnabTokenRenewalUnavailableException("YNAB could not be reached to renew the connection, so the request was not sent.");
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// Call with the gate held. Tokens this deployment cannot decrypt are useless to it.
	private async Task<Snapshot> DiscardUnreadableAsync(YnabOAuthConnectionEntity row, CancellationToken cancellationToken)
	{
		logger.LogWarning(
			"The stored YNAB tokens for connection {ConnectionId} could not be decrypted, so they were discarded and an Admin must reconnect. This is expected after moving the database to a host with different Data Protection keys.",
			row.ConnectionId);
		await repository.MarkReconnectRequiredAsync(row.ConnectionId, timeProvider.GetUtcNow(), cancellationToken);
		return ReconnectSnapshot(row);
	}

	private static Snapshot ConnectedSnapshot(YnabOAuthConnectionEntity row, string accessToken, DateTimeOffset? expiresAtUtc) =>
		new(YnabOAuthConnectionStatus.Connected, row.ConnectionId, row.ConnectedAtUtc, accessToken, expiresAtUtc - RenewalSkew);

	private static Snapshot ReconnectSnapshot(YnabOAuthConnectionEntity row) =>
		new(YnabOAuthConnectionStatus.ReconnectRequired, row.ConnectionId, null, null, null);

	// Cached YNAB data is namespaced by connection, so reconnecting never serves data fetched
	// under an earlier connection.
	private static YnabBearerCredential OAuthCredential(string accessToken, Guid connectionId) =>
		new(accessToken, $"{OAuthIdentityPrefix}{connectionId:N}");

	// The first 8 hex chars of the PAT's SHA-256 namespace cached data by PAT identity, so a
	// rotated PAT never serves the previous one's data, without keeping the token in a key.
	private YnabBearerCredential? PatCredential()
	{
		string? pat = Pat;
		if (string.IsNullOrWhiteSpace(pat))
		{
			return null;
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(pat));
		return new YnabBearerCredential(pat, Convert.ToHexString(hash)[..8]);
	}
}
