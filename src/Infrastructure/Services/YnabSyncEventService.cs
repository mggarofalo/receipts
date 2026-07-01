using System.Linq.Expressions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class YnabSyncEventService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ICurrentUserAccessor currentUserAccessor,
	TimeProvider timeProvider) : IYnabSyncEventService
{
	private static readonly Dictionary<string, Expression<Func<YnabSyncEventEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["occurredAt"] = e => e.OccurredAt,
		["eventType"] = e => e.EventType,
		["success"] = e => e.Success,
		["httpStatus"] = e => e.HttpStatus!,
	};

	private static readonly Expression<Func<YnabSyncEventEntity, object>> DefaultSort = e => e.OccurredAt;

	public async Task WriteAsync(
		YnabSyncEventType eventType,
		bool success,
		Guid? receiptId = null,
		Guid? transactionId = null,
		int? httpStatus = null,
		string? errorMessage = null,
		string? requestId = null,
		CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		context.YnabSyncEvents.Add(new YnabSyncEventEntity
		{
			Id = Guid.NewGuid(),
			UserId = currentUserAccessor.UserId,
			OccurredAt = timeProvider.GetUtcNow(),
			EventType = eventType,
			ReceiptId = receiptId,
			TransactionId = transactionId,
			HttpStatus = httpStatus,
			Success = success,
			ErrorMessage = errorMessage,
			RequestId = requestId,
		});
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<PagedResult<YnabSyncEventDto>> GetRecentAsync(
		int offset,
		int limit,
		SortParams sort,
		bool? success = null,
		DateTimeOffset? dateFrom = null,
		DateTimeOffset? dateTo = null,
		CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<YnabSyncEventEntity> query = context.YnabSyncEvents.AsQueryable();

		if (success.HasValue)
		{
			query = query.Where(e => e.Success == success.Value);
		}

		if (dateFrom.HasValue)
		{
			query = query.Where(e => e.OccurredAt >= dateFrom.Value);
		}

		if (dateTo.HasValue)
		{
			// Include the entire end day by moving to the start of the next day.
			DateTimeOffset endOfDay = new(dateTo.Value.Date.AddDays(1), dateTo.Value.Offset);
			query = query.Where(e => e.OccurredAt < endOfDay);
		}

		int total = await query.CountAsync(cancellationToken);

		List<YnabSyncEventDto> data = await query
			.ApplySort(sort, AllowedSortColumns, DefaultSort, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(e => ToDto(e))
			.ToListAsync(cancellationToken);

		return new PagedResult<YnabSyncEventDto>(data, total, offset, limit);
	}

	public async Task<YnabStatus> GetStatusAsync(bool isConfigured, CancellationToken cancellationToken = default)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		DateTimeOffset now = timeProvider.GetUtcNow();
		DateTimeOffset since24h = now.AddHours(-24);
		DateTimeOffset since7d = now.AddDays(-7);
		DateTimeOffset since30d = now.AddDays(-30);

		IQueryable<YnabSyncEventEntity> pushes = context.YnabSyncEvents
			.Where(e => e.EventType == YnabSyncEventType.Push);

		DateTimeOffset? lastValidatedAt = await context.YnabSyncEvents
			.Where(e => e.EventType == YnabSyncEventType.Validate && e.Success)
			.OrderByDescending(e => e.OccurredAt)
			.Select(e => (DateTimeOffset?)e.OccurredAt)
			.FirstOrDefaultAsync(cancellationToken);

		DateTimeOffset? lastPushSuccessAt = await pushes
			.Where(e => e.Success)
			.OrderByDescending(e => e.OccurredAt)
			.Select(e => (DateTimeOffset?)e.OccurredAt)
			.FirstOrDefaultAsync(cancellationToken);

		DateTimeOffset? lastPushFailureAt = await pushes
			.Where(e => !e.Success)
			.OrderByDescending(e => e.OccurredAt)
			.Select(e => (DateTimeOffset?)e.OccurredAt)
			.FirstOrDefaultAsync(cancellationToken);

		int count24h = await pushes.CountAsync(e => e.OccurredAt >= since24h, cancellationToken);
		int count7d = await pushes.CountAsync(e => e.OccurredAt >= since7d, cancellationToken);
		int count30d = await pushes.CountAsync(e => e.OccurredAt >= since30d, cancellationToken);
		int success30d = await pushes.CountAsync(e => e.OccurredAt >= since30d && e.Success, cancellationToken);
		int failure30d = count30d - success30d;

		return new YnabStatus(
			isConfigured,
			lastValidatedAt,
			lastPushSuccessAt,
			lastPushFailureAt,
			count24h,
			count7d,
			count30d,
			success30d,
			failure30d);
	}

	private static YnabSyncEventDto ToDto(YnabSyncEventEntity e) => new(
		e.Id,
		e.OccurredAt,
		e.EventType.ToString(),
		e.ReceiptId,
		e.TransactionId,
		e.HttpStatus,
		e.Success,
		e.ErrorMessage,
		e.RequestId);
}
