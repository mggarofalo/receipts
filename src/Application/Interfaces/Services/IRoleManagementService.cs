using Application.Models;

namespace Application.Interfaces.Services;

public interface IRoleManagementService
{
	/// <summary>
	/// Changes role membership and invalidates old JWT claims atomically. A supplied
	/// profile update participates in the same transaction as replacement roles.
	/// </summary>
	Task<RoleChangeResult> ChangeAsync(
		string userId,
		string actorId,
		RoleChangeMode mode,
		IReadOnlyCollection<string> roles,
		UserProfileUpdate? profile = null,
		CancellationToken cancellationToken = default);
}
