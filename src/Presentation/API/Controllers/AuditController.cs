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
[Route("api/audit")]
[Produces("application/json")]
[Authorize(Policy = "RequireAdmin")]
public class AuditController(IAuditService auditService) : ControllerBase
{
	[HttpGet("")]
	public async Task<Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<string>>> GetAuditLogs(
		[FromQuery] string? entityType = null,
		[FromQuery] string? entityId = null,
		[FromQuery] string? userId = null,
		[FromQuery] Guid? apiKeyId = null,
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

		if (sortBy is not null && !SortableColumns.AuditLog.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.AuditLog)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);

		PagedResult<AuditLogDto> result;
		if (entityType != null && entityId != null)
		{
			result = await auditService.GetByEntityAsync(entityType, entityId, offset, limit, sort, cancellationToken);
		}
		else if (userId != null)
		{
			result = await auditService.GetByUserAsync(userId, offset, limit, sort, cancellationToken);
		}
		else if (apiKeyId.HasValue)
		{
			result = await auditService.GetByApiKeyAsync(apiKeyId.Value, offset, limit, sort, cancellationToken);
		}
		else
		{
			return TypedResults.BadRequest("At least one filter parameter (entityType+entityId, userId, or apiKeyId) is required.");
		}

		return TypedResults.Ok(ToListResponse(result));
	}

	[HttpGet("recent")]
	public async Task<Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<string>>> GetRecent(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null,
		[FromQuery] string? entityType = null,
		[FromQuery] string? action = null,
		[FromQuery] string? search = null,
		[FromQuery] DateTimeOffset? dateFrom = null,
		[FromQuery] DateTimeOffset? dateTo = null,
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

		if (sortBy is not null && !SortableColumns.AuditLog.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.AuditLog)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<AuditLogDto> result = await auditService.GetRecentAsync(offset, limit, sort, entityType, action, search, dateFrom, dateTo, cancellationToken);
		return TypedResults.Ok(ToListResponse(result));
	}

	private static GeneratedDtos.AuditLogListResponse ToListResponse(PagedResult<AuditLogDto> result) => new()
	{
		Data = [.. result.Data.Select(d => new GeneratedDtos.AuditLogDto
		{
			Id = d.Id,
			EntityType = d.EntityType,
			EntityId = d.EntityId,
			Action = d.Action,
			ChangesJson = d.ChangesJson,
			ChangedByUserId = d.ChangedByUserId,
			ChangedByApiKeyId = d.ChangedByApiKeyId,
			ChangedAt = d.ChangedAt,
			IpAddress = d.IpAddress,
		})],
		Total = result.Total,
		Offset = result.Offset,
		Limit = result.Limit,
	};
}
