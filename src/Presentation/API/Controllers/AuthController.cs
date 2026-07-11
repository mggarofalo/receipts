using System.Security.Claims;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Asp.Versioning;
using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
[EnableRateLimiting("auth")]
public class AuthController(
	UserManager<ApplicationUser> userManager,
	ITokenService tokenService,
	IUserService userService,
	IAuthAuditService authAuditService,
	ILogger<AuthController> logger) : ControllerBase
{
	[HttpPost("login")]
	[AllowAnonymous]
	[EndpointSummary("Login with email and password")]
	[ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status401Unauthorized)]
	public async Task<Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>>> Login([FromBody] LoginRequest request)
	{
		ApplicationUser? user = await userManager.FindByEmailAsync(request.Email);
		if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
		{
			await LogAuthEventAsync(nameof(AuthEventType.LoginFailed), user?.Id, request.Email, false, "Invalid credentials");
			return TypedResults.Json(new OAuthErrorResponse
			{
				Error = OAuthErrorResponseError.Invalid_grant,
				Error_description = "Invalid email or password",
			}, statusCode: StatusCodes.Status401Unauthorized);
		}

		if (await userManager.IsLockedOutAsync(user))
		{
			await LogAuthEventAsync(nameof(AuthEventType.LoginFailed), user.Id, request.Email, false, "Account disabled");
			return TypedResults.Json(new OAuthErrorResponse
			{
				Error = OAuthErrorResponseError.Invalid_grant,
				Error_description = "Account is disabled",
			}, statusCode: StatusCodes.Status401Unauthorized);
		}

		IList<string> roles = await userManager.GetRolesAsync(user);
		string accessToken = tokenService.GenerateAccessToken(user.Id, user.Email!, roles, user.MustResetPassword);
		string refreshToken = tokenService.GenerateRefreshToken();

		// Persist only the hash of the refresh token; the plaintext is returned to the client below and
		// never stored, so a leaked database/backup cannot yield a usable token.
		user.RefreshToken = userService.HashRefreshToken(refreshToken);
		user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30);
		user.LastLoginAt = DateTimeOffset.UtcNow;

		IdentityResult updateResult = await userManager.UpdateAsync(user);
		if (!updateResult.Succeeded)
		{
			// The session (refresh token) was not persisted — e.g. a concurrency-stamp mismatch. Fail
			// closed with a generic 401 rather than handing back a token the database never stored.
			await LogAuthEventAsync(nameof(AuthEventType.LoginFailed), user.Id, request.Email, false, "Failed to persist session");
			return TypedResults.Json(new OAuthErrorResponse
			{
				Error = OAuthErrorResponseError.Invalid_grant,
				Error_description = "Invalid email or password",
			}, statusCode: StatusCodes.Status401Unauthorized);
		}

		await LogAuthEventAsync(nameof(AuthEventType.Login), user.Id, user.Email, true);

		return TypedResults.Ok(new TokenResponse
		{
			AccessToken = accessToken,
			RefreshToken = refreshToken,
			ExpiresIn = 3600,
			MustResetPassword = user.MustResetPassword,
			TokenType = "Bearer",
			Scope = string.Join(" ", roles),
		});
	}

	[HttpPost("refresh")]
	[AllowAnonymous]
	[EnableRateLimiting("auth-sensitive")]
	[EndpointSummary("Refresh access token")]
	public async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
	{
		string? userId = await userService.FindUserIdByRefreshTokenAsync(request.RefreshToken, cancellationToken);
		ApplicationUser? user = userId is not null ? await userManager.FindByIdAsync(userId) : null;

		if (user is null
			|| user.RefreshTokenExpiresAt is null
			|| user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
		{
			return TypedResults.Unauthorized();
		}

		IList<string> roles = await userManager.GetRolesAsync(user);
		string accessToken = tokenService.GenerateAccessToken(user.Id, user.Email!, roles, user.MustResetPassword);
		string newRefreshToken = tokenService.GenerateRefreshToken();

		user.RefreshToken = userService.HashRefreshToken(newRefreshToken);
		user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30);

		IdentityResult updateResult = await userManager.UpdateAsync(user);
		if (!updateResult.Succeeded)
		{
			// Two refresh requests raced: the stored ConcurrencyStamp changed under us, so this rotation
			// was NOT persisted. Returning the new token would hand back one the database never saved.
			// Fail closed (invalid_grant) so the loser retries with a fresh, valid token.
			return TypedResults.Unauthorized();
		}

		return TypedResults.Ok(new TokenResponse
		{
			AccessToken = accessToken,
			RefreshToken = newRefreshToken,
			ExpiresIn = 3600,
			MustResetPassword = user.MustResetPassword,
			TokenType = "Bearer",
			Scope = string.Join(" ", roles),
		});
	}

	[HttpPost("logout")]
	[Authorize]
	[EndpointSummary("Logout and invalidate refresh token")]
	public async Task<Results<NoContent, UnauthorizedHttpResult>> Logout()
	{
		string? userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (userId is null)
		{
			return TypedResults.Unauthorized();
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is not null)
		{
			user.RefreshToken = null;
			user.RefreshTokenExpiresAt = null;
			await userManager.UpdateAsync(user);
		}

		await LogAuthEventAsync(nameof(AuthEventType.Logout), userId, user?.Email, true);

		return TypedResults.NoContent();
	}

	[HttpPost("change-password")]
	[Authorize]
	[EnableRateLimiting("auth-sensitive")]
	[EndpointSummary("Change password (required on first login)")]
	[ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status400BadRequest)]
	public async Task<Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>, UnauthorizedHttpResult>> ChangePassword([FromBody] ChangePasswordRequest request)
	{
		string? userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (userId is null)
		{
			return TypedResults.Unauthorized();
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.Unauthorized();
		}

		IdentityResult result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
		if (!result.Succeeded)
		{
			return TypedResults.Json(new OAuthErrorResponse
			{
				Error = OAuthErrorResponseError.Invalid_request,
				Error_description = string.Join("; ", result.Errors.Select(e => e.Description)),
			}, statusCode: StatusCodes.Status400BadRequest);
		}

		user.MustResetPassword = false;

		IList<string> roles = await userManager.GetRolesAsync(user);
		string accessToken = tokenService.GenerateAccessToken(user.Id, user.Email!, roles, false);
		string refreshToken = tokenService.GenerateRefreshToken();

		user.RefreshToken = userService.HashRefreshToken(refreshToken);
		user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30);

		IdentityResult sessionUpdateResult = await userManager.UpdateAsync(user);
		if (!sessionUpdateResult.Succeeded)
		{
			// The password change itself already persisted; only the new session failed to save (e.g. a
			// concurrency-stamp mismatch). Fail closed so the caller re-authenticates with the new password.
			return TypedResults.Unauthorized();
		}

		await LogAuthEventAsync(nameof(AuthEventType.PasswordChanged), user.Id, user.Email, true);

		return TypedResults.Ok(new TokenResponse
		{
			AccessToken = accessToken,
			RefreshToken = refreshToken,
			ExpiresIn = 3600,
			MustResetPassword = false,
			TokenType = "Bearer",
			Scope = string.Join(" ", roles),
		});
	}

	[HttpPost("introspect")]
	[Authorize]
	[EndpointSummary("Introspect a token per RFC 7662")]
	public async Task<Ok<TokenIntrospectionResponse>> IntrospectToken(
		[FromBody] TokenIntrospectionRequest request,
		CancellationToken cancellationToken)
	{
		if (request.TokenTypeHint == TokenIntrospectionRequestTokenTypeHint.RefreshToken)
		{
			string? userId = await userService.FindUserIdByRefreshTokenAsync(request.Token, cancellationToken);
			ApplicationUser? user = userId is not null ? await userManager.FindByIdAsync(userId) : null;

			if (user is null
				|| user.RefreshTokenExpiresAt is null
				|| user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
			{
				return TypedResults.Ok(new TokenIntrospectionResponse { Active = false });
			}

			IList<string> roles = await userManager.GetRolesAsync(user);

			return TypedResults.Ok(new TokenIntrospectionResponse
			{
				Active = true,
				Scope = string.Join(" ", roles),
				Username = user.Email ?? string.Empty,
				TokenType = "refresh_token",
				Exp = user.RefreshTokenExpiresAt.Value.ToUnixTimeSeconds(),
				Sub = user.Id,
			});
		}

		TokenIntrospectionResult introspection = tokenService.IntrospectAccessToken(request.Token);

		return TypedResults.Ok(new TokenIntrospectionResponse
		{
			Active = introspection.Active,
			Scope = introspection.Scope ?? string.Empty,
			Username = introspection.Username ?? string.Empty,
			TokenType = introspection.TokenType ?? string.Empty,
			Exp = introspection.Exp ?? 0,
			Iat = introspection.Iat ?? 0,
			Sub = introspection.Sub ?? string.Empty,
		});
	}

	[HttpPost("revoke")]
	[Authorize]
	[EndpointSummary("Revoke a token per RFC 7009")]
	public async Task<Ok> RevokeToken(
		[FromBody] TokenRevocationRequest request,
		CancellationToken cancellationToken)
	{
		// Per RFC 7009, always return 200 regardless of whether the token was found
		string? userId = await userService.FindUserIdByRefreshTokenAsync(request.Token, cancellationToken);
		if (userId is not null)
		{
			ApplicationUser? user = await userManager.FindByIdAsync(userId);
			if (user is not null)
			{
				user.RefreshToken = null;
				user.RefreshTokenExpiresAt = null;
				await userManager.UpdateAsync(user);

				await LogAuthEventAsync(nameof(AuthEventType.TokenRevoked), user.Id, user.Email, true);
			}
		}

		return TypedResults.Ok();
	}

	private async Task LogAuthEventAsync(string eventType, string? userId, string? username, bool success, string? failureReason = null)
	{
		try
		{
			await authAuditService.LogAsync(new Application.Interfaces.Services.AuthAuditEntryDto(
				Guid.NewGuid(),
				eventType,
				userId,
				null,
				username,
				success,
				failureReason,
				HttpContext.Connection.RemoteIpAddress?.ToString(),
				Request.Headers.UserAgent.ToString(),
				DateTimeOffset.UtcNow,
				null));
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to log auth audit event {EventType}", eventType);
		}
	}
}
