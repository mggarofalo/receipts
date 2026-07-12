using System.Security.Claims;
using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;

namespace API.Authentication;

/// <summary>
/// Per-request revalidation of a JWT access token against the owning user's live state.
/// A JWT carries baked-in role claims and, by default, is trusted until it expires — so an access
/// token issued before a user is deactivated or has their password reset would keep working. This
/// re-checks the token's <c>security_stamp</c> claim (and lockout state) against the database on
/// every authenticated request, mirroring the API-key handler's per-request account re-check
/// (RECEIPTS-757, RECEIPTS-800). Costs one DB read per authenticated request, which is acceptable.
/// </summary>
public static class JwtSecurityStampValidator
{
	/// <summary>
	/// <see cref="JwtBearerEvents.OnTokenValidated"/> handler: fails the authentication when the token's
	/// security stamp no longer matches the user's live stamp (or the account is gone / locked out).
	/// </summary>
	public static async Task RevalidateAsync(TokenValidatedContext context)
	{
		UserManager<ApplicationUser> userManager = context.HttpContext.RequestServices
			.GetRequiredService<UserManager<ApplicationUser>>();

		string? userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		string? tokenStamp = context.Principal?.FindFirst(AuthClaimTypes.SecurityStamp)?.Value;

		SecurityStampRevalidationResult result = await EvaluateAsync(userManager, userId, tokenStamp);
		if (!result.IsValid)
		{
			context.Fail(result.FailureReason!);
		}
	}

	/// <summary>
	/// Core comparison, split out from the event handler so it can be unit-tested with a mocked
	/// <see cref="UserManager{TUser}"/> without constructing a full HTTP pipeline / token context.
	/// </summary>
	public static async Task<SecurityStampRevalidationResult> EvaluateAsync(
		UserManager<ApplicationUser> userManager,
		string? userId,
		string? tokenStamp)
	{
		if (string.IsNullOrEmpty(userId))
		{
			return SecurityStampRevalidationResult.Invalid("Token is missing the subject (sub) claim.");
		}

		// Tokens minted before this feature shipped carry no security_stamp claim. Treat an absent stamp
		// as invalid so those sessions fail closed; the user simply logs in once after deploy (RECEIPTS-800).
		if (string.IsNullOrEmpty(tokenStamp))
		{
			return SecurityStampRevalidationResult.Invalid("Token is missing the security_stamp claim.");
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return SecurityStampRevalidationResult.Invalid("Token subject no longer exists.");
		}

		if (await userManager.IsLockedOutAsync(user))
		{
			return SecurityStampRevalidationResult.Invalid("Account is disabled or locked out.");
		}

		if (!string.Equals(tokenStamp, user.SecurityStamp, StringComparison.Ordinal))
		{
			return SecurityStampRevalidationResult.Invalid("Security stamp has changed; token is no longer valid.");
		}

		return SecurityStampRevalidationResult.Valid;
	}
}

/// <summary>Outcome of <see cref="JwtSecurityStampValidator.EvaluateAsync"/>.</summary>
public sealed record SecurityStampRevalidationResult(bool IsValid, string? FailureReason)
{
	public static readonly SecurityStampRevalidationResult Valid = new(true, null);

	public static SecurityStampRevalidationResult Invalid(string reason) => new(false, reason);
}
