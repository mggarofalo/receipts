using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabSyncEventRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabSyncEventRepository
{
	public async Task<YnabSyncEventEntity> CreateAsync(YnabSyncEventEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.YnabSyncEvents.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<(IReadOnlyList<YnabSyncEventEntity> Events, int TotalCount)> ListAsync(
		int offset,
		int limit,
		YnabSyncStatus? outcome,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		IQueryable<YnabSyncEventEntity> query = context.YnabSyncEvents.AsNoTracking();
		if (outcome is { } o)
		{
			query = query.Where(e => e.Outcome == o);
		}

		int total = await query.CountAsync(cancellationToken);
		List<YnabSyncEventEntity> events = await query
			.OrderByDescending(e => e.OccurredAt)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);

		return (events, total);
	}

	public async Task<DateTimeOffset?> GetLatestOccurrenceAsync(YnabSyncStatus outcome, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncEvents
			.AsNoTracking()
			.Where(e => e.Outcome == outcome)
			.OrderByDescending(e => e.OccurredAt)
			.Select(e => (DateTimeOffset?)e.OccurredAt)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task<int> CountSinceAsync(DateTimeOffset since, YnabSyncStatus? outcome, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<YnabSyncEventEntity> query = context.YnabSyncEvents
			.AsNoTracking()
			.Where(e => e.OccurredAt >= since);

		if (outcome is { } o)
		{
			query = query.Where(e => e.Outcome == o);
		}

		return await query.CountAsync(cancellationToken);
	}
}
