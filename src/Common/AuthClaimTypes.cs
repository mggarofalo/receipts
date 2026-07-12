namespace Common;

/// <summary>
/// Custom (non-standard) JWT claim types that the application both issues and validates.
/// </summary>
public static class AuthClaimTypes
{
	/// <summary>
	/// Carries the user's ASP.NET Identity <c>SecurityStamp</c> at the moment the access token was issued.
	/// Every authenticated request re-compares this against the user's live stamp; rotating the stamp
	/// (on deactivation, disable, or password reset) makes all previously-issued access tokens fail
	/// revalidation immediately, so a session cannot outlive the state change that should have ended it.
	/// </summary>
	public const string SecurityStamp = "security_stamp";
}
