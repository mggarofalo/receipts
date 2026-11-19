using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Ynab.OAuth;

public sealed record YnabAuthorizationAttempt(
	string State, string CodeVerifier, string CodeChallenge, string UserId, DateTimeOffset ExpiresAtUtc)
{
	// A record's generated ToString would print the state and verifier.
	public override string ToString() => $"{nameof(YnabAuthorizationAttempt)} {{ ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

/// <summary>
/// Authorization attempts that have been started and not yet completed. Held in memory only:
/// the app is one process, the PKCE verifier never needs to reach disk, and a restart
/// mid-flow costs the Admin one more click.
/// </summary>
public class YnabAuthorizationAttemptStore(TimeProvider timeProvider)
{
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

	// Far more than one deployment's Admins can have in flight; bounds memory if something loops.
	private const int MaxAttempts = 32;

	private readonly ConcurrentDictionary<string, YnabAuthorizationAttempt> _attempts = new(StringComparer.Ordinal);

	public YnabAuthorizationAttempt Create(string userId)
	{
		Prune();

		string state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		string codeVerifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
		string codeChallenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

		YnabAuthorizationAttempt attempt = new(state, codeVerifier, codeChallenge, userId, timeProvider.GetUtcNow() + Lifetime);
		_attempts[Key(state)] = attempt;
		return attempt;
	}

	/// <summary>
	/// Removes and returns the attempt for <paramref name="state"/> when it exists, has not
	/// expired and was started by <paramref name="userId"/>. Taking is what makes an attempt
	/// single-use: of two simultaneous completions, only one gets it.
	/// </summary>
	public YnabAuthorizationAttempt? Take(string state, string userId)
	{
		string key = Key(state);
		if (!_attempts.TryGetValue(key, out YnabAuthorizationAttempt? attempt))
		{
			return null;
		}

		if (attempt.ExpiresAtUtc <= timeProvider.GetUtcNow())
		{
			_attempts.TryRemove(KeyValuePair.Create(key, attempt));
			return null;
		}

		// Another Admin's attempt stays where it is, for the Admin who started it.
		if (!string.Equals(attempt.UserId, userId, StringComparison.Ordinal))
		{
			return null;
		}

		return _attempts.TryRemove(KeyValuePair.Create(key, attempt)) ? attempt : null;
	}

	/// <summary>
	/// Puts back an attempt whose completion could not reach YNAB, so it can be tried again
	/// until it expires. YNAB does not consume a code on a request it never answered.
	/// </summary>
	public void Restore(YnabAuthorizationAttempt attempt)
	{
		if (attempt.ExpiresAtUtc > timeProvider.GetUtcNow())
		{
			_attempts.TryAdd(Key(attempt.State), attempt);
		}
	}

	// Keyed by a hash so the map never holds a state value as a key.
	private static string Key(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

	private void Prune()
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		foreach (KeyValuePair<string, YnabAuthorizationAttempt> entry in _attempts)
		{
			if (entry.Value.ExpiresAtUtc <= now)
			{
				_attempts.TryRemove(entry);
			}
		}

		while (_attempts.Count >= MaxAttempts)
		{
			KeyValuePair<string, YnabAuthorizationAttempt> oldest = _attempts.MinBy(entry => entry.Value.ExpiresAtUtc);
			if (oldest.Key is null || !_attempts.TryRemove(oldest))
			{
				break;
			}
		}
	}
}
