namespace Application.Models.Ynab;

/// <summary>Which credential, if any, YNAB requests are sent with.</summary>
public enum YnabCredentialKind
{
	None,
	PersonalAccessToken,
	OAuth,
	/// <summary>An OAuth connection existed and was rejected or became unreadable; an Admin must reconnect.</summary>
	OAuthReconnectRequired,
}

/// <param name="OAuthAvailable">Whether this deployment has the OAuth client settings needed to start a connection.</param>
public sealed record YnabCredentialState(
	YnabCredentialKind Kind,
	bool OAuthAvailable = false,
	DateTimeOffset? OAuthConnectedAtUtc = null)
{
	/// <summary>True when YNAB requests can be sent.</summary>
	public bool IsUsable => Kind is YnabCredentialKind.PersonalAccessToken or YnabCredentialKind.OAuth;
}

/// <param name="Token">The bearer token. Never log or return it.</param>
/// <param name="CacheIdentity">Stable, non-secret identity of the credential, used to namespace cached YNAB data.</param>
public sealed record YnabBearerCredential(string Token, string CacheIdentity)
{
	// A record's generated ToString would print the token.
	public override string ToString() => $"{nameof(YnabBearerCredential)} {{ CacheIdentity = {CacheIdentity} }}";
}
