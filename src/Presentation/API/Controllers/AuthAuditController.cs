using System.Security.Claims;
using Application.Interfaces.Services;
using Application.Models;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using GeneratedDtos = API.Generated.Dtos;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/auth/audit")]
[Produces("application/json")]
[Authorize]
public class AuthAuditController(IAuthAuditService authAuditService) : ControllerBase
{
	[HttpGet("me")]
	public async Task<Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<string>, UnauthorizedHttpResult>> GetMyAuditLog(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null,
		CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be non-negative");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		string? userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (userId is null)
		{
			return TypedResults.Unauthorized();
		}

		if (sortBy is not null && !SortableColumns.AuthAudit.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.AuthAudit)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<AuthAuditEntryDto> result = await authAuditService.GetMyAuditLogAsync(userId, offset, limit, sort, cancellationToken);
		return TypedResults.Ok(ToListResponse(result));
	}

	[HttpGet("recent")]
	[Authorize(Policy = "RequireAdmin")]
	public async Task<Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<string>>> GetRecent(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null,
		CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be non-negative");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.AuthAudit.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.AuthAudit)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<AuthAuditEntryDto> result = await authAuditService.GetRecentAsync(offset, limit, sort, cancellationToken);
		return TypedResults.Ok(ToListResponse(result));
	}

	[HttpGet("failed")]
	[Authorize(Policy = "RequireAdmin")]
	public async Task<Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<string>>> GetFailed(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null,
		CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be non-negative");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.AuthAudit.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.AuthAudit)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<AuthAuditEntryDto> result = await authAuditService.GetFailedAttemptsAsync(offset, limit, sort, cancellationToken);
		return TypedResults.Ok(ToListResponse(result));
	}

	private static GeneratedDtos.AuthAuditListResponse ToListResponse(PagedResult<AuthAuditEntryDto> result) => new()
	{
		Data = [.. result.Data.Select(d => new GeneratedDtos.AuthAuditEntryDto
		{
			Id = d.Id,
			EventType = d.EventType,
			UserId = d.UserId,
			ApiKeyId = d.ApiKeyId,
			Username = d.Username,
			Success = d.Success,
			FailureReason = d.FailureReason,
			IpAddress = d.IpAddress,
			UserAgent = d.UserAgent,
			Timestamp = d.Timestamp,
			MetadataJson = d.MetadataJson,
		})],
		Total = result.Total,
		Offset = result.Offset,
		Limit = result.Limit,
	};
}
