using System.Security.Claims;
using Common;

namespace API.Authentication;

internal static class RoleChangeActor
{
	public const string InvalidCredentials = "Use administrator credentials for a single user when changing roles.";

	public static string? GetSubject(ClaimsPrincipal principal)
	{
		ClaimsIdentity[] identities = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
		string[] subjects = identities
			.Select(identity => identity.FindFirst(ClaimTypes.NameIdentifier)?.Value)
			.Where(subject => !string.IsNullOrEmpty(subject))
			.Cast<string>()
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		// ASP.NET can combine a JWT and an API key. Never use one subject's
		// identifier with another subject's Admin authority for self-protection.
		if (subjects.Length != 1)
		{
			return null;
		}

		return identities.Any(identity =>
			identity.FindFirst(ClaimTypes.NameIdentifier)?.Value == subjects[0] &&
			identity.HasClaim(identity.RoleClaimType, AppRoles.Admin)) ? subjects[0] : null;
	}
}
