using System.Security.Claims;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
using Asp.Versioning;
using Common;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/users")]
[Produces("application/json")]
[Authorize(Policy = "RequireAdmin")]
public class UsersController(
	IUserService userService,
	UserManager<ApplicationUser> userManager,
	IAuthAuditService authAuditService,
	IApiKeyService apiKeyService,
	ILogger<UsersController> logger) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("List all users with their roles")]
	public async Task<Results<Ok<UserListResponse>, BadRequest<ProblemDetails>>> ListUsers(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.User.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.User)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<UserSummary> result = await userService.ListUsersAsync(offset, limit, sort);

		List<UserSummaryResponse> items = result.Data.Select(MapToResponse).ToList();

		return TypedResults.Ok(new UserListResponse
		{
			Data = items,
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet("{userId}")]
	[EndpointSummary("Get a user by ID")]
	public async Task<Results<Ok<UserSummaryResponse>, NotFound>> GetUser(string userId)
	{
		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		IList<string> roles = await userManager.GetRolesAsync(user);

		return TypedResults.Ok(new UserSummaryResponse
		{
			Id = user.Id,
			Email = user.Email ?? "",
			FirstName = user.FirstName,
			LastName = user.LastName,
			Roles = [.. roles],
			IsDisabled = user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow,
			CreatedAt = user.CreatedAt,
			LastLoginAt = user.LastLoginAt,
		});
	}

	[HttpPost]
	[EndpointSummary("Create a new user (admin only)")]
	public async Task<Results<Ok<UserSummaryResponse>, BadRequest<ProblemDetails>>> CreateUser([FromBody] CreateUserRequest request)
	{
		ApplicationUser user = new()
		{
			UserName = request.Email,
			Email = request.Email,
			FirstName = request.FirstName,
			LastName = request.LastName,
			MustResetPassword = true,
			CreatedAt = DateTimeOffset.UtcNow,
		};

		IdentityResult result = await userManager.CreateAsync(user, request.Password);
		if (!result.Succeeded)
		{
			return ApiProblem.BadRequest(result.Errors.Select(e => e.Description));
		}

		IdentityResult roleResult = await userManager.AddToRoleAsync(user, request.Role);
		if (!roleResult.Succeeded)
		{
			return ApiProblem.BadRequest(roleResult.Errors.Select(e => e.Description));
		}

		await LogAuthEventAsync(nameof(AuthEventType.UserRegistered), user.Id, user.Email);

		return TypedResults.Ok(new UserSummaryResponse
		{
			Id = user.Id,
			Email = user.Email!,
			FirstName = user.FirstName,
			LastName = user.LastName,
			Roles = [request.Role],
			IsDisabled = false,
			CreatedAt = user.CreatedAt,
			LastLoginAt = null,
		});
	}

	[HttpPut("{userId}")]
	[EndpointSummary("Update a user (admin only)")]
	// One BadRequest arm, not two: the signature previously distinguished a bare-string
	// rejection from an Identity error list, and both are now the same problem document.
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>>> UpdateUser(string userId, [FromBody] UpdateUserRequest request)
	{
		string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		if (userId == currentUserId)
		{
			if (request.IsDisabled)
			{
				return ApiProblem.BadRequest("Cannot disable your own account.");
			}

			IList<string> currentRoles = await userManager.GetRolesAsync(user);
			if (currentRoles.Contains("Admin") && request.Role != "Admin")
			{
				return ApiProblem.BadRequest("Cannot remove your own Admin role.");
			}
		}

		user.Email = request.Email;
		user.UserName = request.Email;
		user.FirstName = request.FirstName;
		user.LastName = request.LastName;

		// Disabled/enabled is signalled by LockoutEnd (MaxValue = disabled, null = enabled), never by
		// LockoutEnabled. LockoutEnabled must stay true so failed-login lockout keeps working — setting it
		// false here would permanently disable brute-force protection for the user, since IsLockedOutAsync
		// short-circuits to false when LockoutEnabled is false (RECEIPTS-776).
		user.LockoutEnabled = true;
		user.LockoutEnd = request.IsDisabled ? DateTimeOffset.MaxValue : null;

		IdentityResult updateResult = await userManager.UpdateAsync(user);
		if (!updateResult.Succeeded)
		{
			return ApiProblem.BadRequest(updateResult.Errors.Select(e => e.Description));
		}

		if (request.IsDisabled)
		{
			// Disabling an account must also cut off any pre-existing API keys, otherwise
			// they keep authenticating with the user's roles indefinitely (RECEIPTS-757).
			await apiKeyService.RevokeAllForUserAsync(user.Id);

			// Rotate the security stamp so any JWT access token issued before this disable fails
			// per-request revalidation immediately, instead of surviving until it expires (RECEIPTS-800).
			await userManager.UpdateSecurityStampAsync(user);
		}

		IList<string> roles = await userManager.GetRolesAsync(user);
		if (roles.Count > 0)
		{
			await userManager.RemoveFromRolesAsync(user, roles);
		}

		IdentityResult roleResult = await userManager.AddToRoleAsync(user, request.Role);
		if (!roleResult.Succeeded)
		{
			return ApiProblem.BadRequest(roleResult.Errors.Select(e => e.Description));
		}

		return TypedResults.NoContent();
	}

	[HttpDelete("{userId}")]
	[EndpointSummary("Deactivate a user (admin only)")]
	public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound>> DeactivateUser(string userId)
	{
		string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (userId == currentUserId)
		{
			return ApiProblem.BadRequest("Cannot deactivate your own account.");
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		user.LockoutEnabled = true;
		user.LockoutEnd = DateTimeOffset.MaxValue;
		user.RefreshToken = null;
		user.RefreshTokenExpiresAt = null;
		await userManager.UpdateAsync(user);

		// Rotate the security stamp so any JWT access token issued before deactivation fails per-request
		// revalidation immediately. Clearing the refresh token alone only stops renewal; without this the
		// existing access token would keep working until it expires (RECEIPTS-800).
		await userManager.UpdateSecurityStampAsync(user);

		// Revoke API keys so a deactivated user cannot keep authenticating via a stale key.
		await apiKeyService.RevokeAllForUserAsync(user.Id);

		await LogAuthEventAsync(nameof(AuthEventType.AccountDisabled), user.Id, user.Email);

		return TypedResults.NoContent();
	}

	[HttpPost("{userId}/reset-password")]
	[EndpointSummary("Reset a user's password (admin only)")]
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>>> AdminResetPassword(string userId, [FromBody] AdminResetPasswordRequest request)
	{
		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		await userManager.RemovePasswordAsync(user);
		IdentityResult result = await userManager.AddPasswordAsync(user, request.NewPassword);
		if (!result.Succeeded)
		{
			return ApiProblem.BadRequest(result.Errors.Select(e => e.Description));
		}

		user.MustResetPassword = true;
		user.RefreshToken = null;
		user.RefreshTokenExpiresAt = null;
		await userManager.UpdateAsync(user);

		// Rotate the security stamp so JWT access tokens minted under the old password fail per-request
		// revalidation immediately (explicit and independent of Identity's internal stamp rotation on
		// password change, RECEIPTS-800).
		await userManager.UpdateSecurityStampAsync(user);

		// Force-resetting a password invalidates the old credential; revoke API keys too so
		// they cannot be used to bypass the reset (defense in depth, RECEIPTS-757).
		await apiKeyService.RevokeAllForUserAsync(user.Id);

		await LogAuthEventAsync(nameof(AuthEventType.PasswordChanged), user.Id, user.Email);

		return TypedResults.NoContent();
	}

	private static UserSummaryResponse MapToResponse(UserSummary user) => new()
	{
		Id = user.Id,
		Email = user.Email,
		FirstName = user.FirstName,
		LastName = user.LastName,
		Roles = [.. user.Roles],
		IsDisabled = user.IsDisabled,
		CreatedAt = user.CreatedAt,
		LastLoginAt = user.LastLoginAt,
	};

	private async Task LogAuthEventAsync(string eventType, string? userId, string? username)
	{
		try
		{
			await authAuditService.LogAsync(new Application.Interfaces.Services.AuthAuditEntryDto(
				Guid.NewGuid(),
				eventType,
				userId,
				null,
				username,
				true,
				null,
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
