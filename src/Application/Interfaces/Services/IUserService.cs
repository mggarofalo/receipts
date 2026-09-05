using Application.Models;

namespace Application.Interfaces.Services;

public record UserSummary(
	string Id,
	string Email,
	string? FirstName,
	string? LastName,
	IReadOnlyList<string> Roles,
	bool IsDisabled,
	DateTimeOffset CreatedAt,
	DateTimeOffset? LastLoginAt);

public interface IUserService
{
	Task<PagedResult<UserSummary>> ListUsersAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken = default);
	Task<string?> FindUserIdByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

	/// <summary>
	/// Computes the SHA-256 hash (lowercase hex) of a refresh token. Only this hash is persisted on the
	/// user row; the plaintext token is returned to the client exactly once at issue time. Mirrors the
	/// hashing used for API keys so a leaked database or backup does not expose usable refresh tokens.
	/// </summary>
	string HashRefreshToken(string refreshToken);

	/// <summary>Atomically revokes only the captured family, fencing concurrent Identity updates.
	/// A null family matches only legacy sessions. Access-token security stamps are unchanged.</summary>
	Task RevokeRefreshSessionAsync(string userId, Guid? refreshSessionId, CancellationToken cancellationToken = default);
}
