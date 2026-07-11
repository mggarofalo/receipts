using System.Linq.Expressions;
using Application.Interfaces.Services;
using Application.Models;
using Infrastructure.Entities.Audit;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class AuditService(IDbContextFactory<ApplicationDbContext> contextFactory) : IAuditService
{
	private static readonly Dictionary<string, Expression<Func<AuditLogEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["changedAt"] = a => a.ChangedAt,
		["entityType"] = a => a.EntityType,
		["action"] = a => a.Action,
		["entityId"] = a => a.EntityId,
	};

	private static readonly Expression<Func<AuditLogEntity, object>> DefaultSort = a => a.ChangedAt;

	public async Task<PagedResult<AuditLogDto>> GetByEntityAsync(string entityType, string entityId, int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuditLogEntity> query = context.AuditLogs
			.Where(a => a.EntityType == entityType && a.EntityId == entityId);

		int total = await query.CountAsync(cancellationToken);

		List<AuditLogDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuditLogDto>(data, total, offset, limit);
	}

	public async Task<PagedResult<AuditLogDto>> GetRecentAsync(int offset, int limit, SortParams sort, string? entityType = null, string? action = null, string? search = null, DateTimeOffset? dateFrom = null, DateTimeOffset? dateTo = null, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuditLogEntity> query = context.AuditLogs.AsQueryable();

		if (!string.IsNullOrWhiteSpace(entityType))
		{
			query = query.Where(a => a.EntityType == entityType);
		}

		if (!string.IsNullOrWhiteSpace(action) && Enum.TryParse<AuditAction>(action, ignoreCase: true, out AuditAction parsedAction))
		{
			query = query.Where(a => a.Action == parsedAction);
		}

		if (!string.IsNullOrWhiteSpace(search))
		{
			query = query.Where(a => a.EntityId.Contains(search));
		}

		if (dateFrom.HasValue)
		{
			query = query.Where(a => a.ChangedAt >= dateFrom.Value);
		}

		if (dateTo.HasValue)
		{
			// Include the entire end day by moving to start of next day
			DateTimeOffset endOfDay = new DateTimeOffset(dateTo.Value.Date.AddDays(1), dateTo.Value.Offset);
			query = query.Where(a => a.ChangedAt < endOfDay);
		}

		int total = await query.CountAsync(cancellationToken);

		List<AuditLogDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuditLogDto>(data, total, offset, limit);
	}

	public async Task<PagedResult<AuditLogDto>> GetByUserAsync(string userId, int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuditLogEntity> query = context.AuditLogs
			.Where(a => a.ChangedByUserId == userId);

		int total = await query.CountAsync(cancellationToken);

		List<AuditLogDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuditLogDto>(data, total, offset, limit);
	}

	public async Task<PagedResult<AuditLogDto>> GetByApiKeyAsync(Guid apiKeyId, int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuditLogEntity> query = context.AuditLogs
			.Where(a => a.ChangedByApiKeyId == apiKeyId);

		int total = await query.CountAsync(cancellationToken);

		List<AuditLogDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuditLogDto>(data, total, offset, limit);
	}

	private static AuditLogDto ToDto(AuditLogEntity entity) => new(
		entity.Id,
		entity.EntityType,
		entity.EntityId,
		entity.Action.ToString(),
		entity.ChangesJson,
		entity.ChangedByUserId,
		entity.ChangedByApiKeyId,
		entity.ChangedAt,
		entity.IpAddress);
}
