using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabOAuthConnectionRepository
{
	Task<YnabOAuthConnectionEntity?> GetAsync(CancellationToken cancellationToken);

	/// <summary>Stores a newly authorized connection, replacing any earlier one.</summary>
	Task<YnabOAuthConnectionEntity> SaveConnectedAsync(
		string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, string? connectedByUserId, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces the tokens of the connection last read with <paramref name="expectedStamp"/>.
	/// Returns false, changing nothing, when the row was changed or removed in the meantime.
	/// </summary>
	Task<bool> TryReplaceTokensAsync(
		Guid expectedStamp, string protectedTokens, DateTimeOffset accessTokenExpiresAtUtc, DateTimeOffset now, CancellationToken cancellationToken);

	/// <summary>
	/// Discards the tokens of connection <paramref name="connectionId"/> and marks it as needing
	/// reconnection. Does nothing when a different connection has since been stored.
	/// </summary>
	Task<bool> MarkReconnectRequiredAsync(Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);

	Task<bool> DeleteAsync(CancellationToken cancellationToken);
}
