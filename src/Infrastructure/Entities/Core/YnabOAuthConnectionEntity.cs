namespace Infrastructure.Entities.Core;

public enum YnabOAuthConnectionStatus
{
	Connected,
	/// <summary>YNAB refused the connection or its tokens became unreadable; no tokens are kept.</summary>
	ReconnectRequired,
}

/// <summary>
/// The deployment's single YNAB OAuth connection. Integration state, not business data: it is
/// never part of the portable backup.
/// </summary>
public class YnabOAuthConnectionEntity
{
	public Guid Id { get; set; }
	public YnabOAuthConnectionStatus Status { get; set; }

	/// <summary>Data Protection payload holding the access and refresh token. Null unless connected.</summary>
	public string? ProtectedTokens { get; set; }

	/// <summary>Kept in the clear so expiry can be checked without decrypting.</summary>
	public DateTimeOffset? AccessTokenExpiresAtUtc { get; set; }

	/// <summary>New on every completed authorization; namespaces cached YNAB data.</summary>
	public Guid ConnectionId { get; set; }

	public DateTimeOffset ConnectedAtUtc { get; set; }
	public string? ConnectedByUserId { get; set; }
	public DateTimeOffset? RefreshedAtUtc { get; set; }
	public DateTimeOffset StatusChangedAtUtc { get; set; }

	/// <summary>Changed on every write; the optimistic concurrency token.</summary>
	public Guid ConcurrencyStamp { get; set; }
}
