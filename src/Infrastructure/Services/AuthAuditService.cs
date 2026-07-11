using System.Linq.Expressions;
using Application.Interfaces.Services;
using Application.Models;
using Common;
using Infrastructure.Entities.Audit;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class AuthAuditService(IDbContextFactory<ApplicationDbContext> contextFactory) : IAuthAuditService
{
	private static readonly Dictionary<string, Expression<Func<AuthAuditLogEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["timestamp"] = a => a.Timestamp,
		["eventType"] = a => a.EventType,
		["success"] = a => a.Success,
	};

	private static readonly Expression<Func<AuthAuditLogEntity, object>> DefaultSort = a => a.Timestamp;

	public async Task LogAsync(AuthAuditEntryDto entry, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		AuthAuditLogEntity entity = new()
		{
			Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
			EventType = Enum.Parse<AuthEventType>(entry.EventType),
			UserId = entry.UserId,
			ApiKeyId = entry.ApiKeyId,
			Username = entry.Username,
			Success = entry.Success,
			FailureReason = entry.FailureReason,
			IpAddress = entry.IpAddress,
			UserAgent = entry.UserAgent,
			Timestamp = entry.Timestamp,
			MetadataJson = entry.MetadataJson,
		};

		context.AuthAuditLogs.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<PagedResult<AuthAuditEntryDto>> GetMyAuditLogAsync(string userId, int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuthAuditLogEntity> query = context.AuthAuditLogs
			.Where(a => a.UserId == userId);

		int total = await query.CountAsync(cancellationToken);

		List<AuthAuditEntryDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuthAuditEntryDto>(data, total, offset, limit);
	}

	public async Task<PagedResult<AuthAuditEntryDto>> GetRecentAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuthAuditLogEntity> query = context.AuthAuditLogs;

		int total = await query.CountAsync(cancellationToken);

		List<AuthAuditEntryDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuthAuditEntryDto>(data, total, offset, limit);
	}

	public async Task<PagedResult<AuthAuditEntryDto>> GetFailedAttemptsAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<AuthAuditLogEntity> query = context.AuthAuditLogs
			.Where(a => !a.Success);

		int total = await query.CountAsync(cancellationToken);

		List<AuthAuditEntryDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, a => a.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(a => ToDto(a))
			.ToListAsync(cancellationToken);

		return new PagedResult<AuthAuditEntryDto>(data, total, offset, limit);
	}

	public async Task<int> CleanupOldEntriesAsync(int retentionDays, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

		return await context.AuthAuditLogs
			.Where(a => a.Timestamp < cutoff)
			.ExecuteDeleteAsync(cancellationToken);
	}

	private static AuthAuditEntryDto ToDto(AuthAuditLogEntity entity) => new(
		entity.Id,
		entity.EventType.ToString(),
		entity.UserId,
		entity.ApiKeyId,
		entity.Username,
		entity.Success,
		entity.FailureReason,
		entity.IpAddress,
		entity.UserAgent,
		entity.Timestamp,
		entity.MetadataJson);
}
