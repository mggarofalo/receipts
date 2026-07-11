using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Interfaces.Services;
using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace API.Authentication;

public static class ApiKeyAuthenticationDefaults
{
	public const string AuthenticationScheme = "ApiKey";
}

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public class ApiKeyAuthenticationHandler(
	IOptionsMonitor<ApiKeyAuthenticationOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder,
	IApiKeyService apiKeyService,
	IAuthAuditService authAuditService,
	UserManager<ApplicationUser> userManager)
	: AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
	private const string ApiKeyHeader = "X-API-Key";

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.Headers.TryGetValue(ApiKeyHeader, out Microsoft.Extensions.Primitives.StringValues apiKeyValues))
		{
			return AuthenticateResult.NoResult();
		}

		string? apiKey = apiKeyValues.FirstOrDefault();
		if (string.IsNullOrWhiteSpace(apiKey))
		{
			return AuthenticateResult.NoResult();
		}

		ApiKeyValidationResult? validationResult = await apiKeyService.GetUserIdByApiKeyAsync(apiKey);
		if (validationResult is null)
		{
			return AuthenticateResult.Fail("Invalid API key.");
		}

		// Re-check the owning account's persisted state on every request. A valid key
		// must not authenticate for a user who has been deleted, deactivated, or locked
		// out since the key was issued (RECEIPTS-757).
		ApplicationUser? user = await userManager.FindByIdAsync(validationResult.UserId);
		if (user is null)
		{
			await LogApiKeyUsageAsync(validationResult, null, false, "API key owner account no longer exists");
			return AuthenticateResult.Fail("API key owner account not found.");
		}

		if (await userManager.IsLockedOutAsync(user))
		{
			await LogApiKeyUsageAsync(validationResult, user.Email, false, "API key owner account is disabled or locked out");
			return AuthenticateResult.Fail("API key owner account is disabled.");
		}

		List<Claim> claims =
		[
			new Claim(ClaimTypes.NameIdentifier, validationResult.UserId),
			new Claim("ApiKeyId", validationResult.KeyId.ToString()),
			new Claim("BypassRateLimit", validationResult.BypassRateLimit.ToString().ToLowerInvariant()),
		];

		if (user.Email is not null)
		{
			claims.Add(new Claim(ClaimTypes.Email, user.Email));
		}

		IList<string> roles = await userManager.GetRolesAsync(user);
		foreach (string role in roles)
		{
			claims.Add(new Claim(ClaimTypes.Role, role));
		}

		ClaimsIdentity identity = new(claims, Scheme.Name);
		ClaimsPrincipal principal = new(identity);
		AuthenticationTicket ticket = new(principal, Scheme.Name);

		await LogApiKeyUsageAsync(validationResult, user.Email, true, null);

		return AuthenticateResult.Success(ticket);
	}

	private async Task LogApiKeyUsageAsync(ApiKeyValidationResult validationResult, string? email, bool success, string? failureReason)
	{
		try
		{
			await authAuditService.LogAsync(new AuthAuditEntryDto(
				Guid.NewGuid(),
				nameof(AuthEventType.ApiKeyUsed),
				validationResult.UserId,
				validationResult.KeyId,
				email,
				success,
				failureReason,
				Context.Connection.RemoteIpAddress?.ToString(),
				Request.Headers.UserAgent.ToString(),
				DateTimeOffset.UtcNow,
				null));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "Failed to log API key usage audit event");
		}
	}
}
