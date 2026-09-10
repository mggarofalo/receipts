using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabSyncRecordRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabSyncRecordRepository
{
	public async Task<YnabSyncRecordEntity> CreateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.YnabSyncRecords.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<YnabSyncRecordEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords.FindAsync([id], cancellationToken);
	}

	public async Task<YnabSyncRecordEntity?> GetByTransactionTypeAndBudgetAsync(
		Guid localTransactionId,
		YnabSyncType syncType,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.FirstOrDefaultAsync(
				e => e.LocalTransactionId == localTransactionId &&
					e.SyncType == syncType &&
					e.YnabBudgetId == ynabBudgetId,
				cancellationToken);
	}

	public async Task<bool> UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? existing = await context.YnabSyncRecords.FindAsync([entity.Id], cancellationToken);
		if (existing is null)
		{
			return false;
		}

		context.Entry(existing).CurrentValues.SetValues(entity);
		return await context.SaveChangesAsync(cancellationToken) > 0;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? entity = await context.YnabSyncRecords.FindAsync([id], cancellationToken);
		if (entity is not null)
		{
			context.YnabSyncRecords.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<List<YnabSyncRecordEntity>> GetByReceiptIdsAndBudgetAsync(
		List<Guid> receiptIds,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.Where(sr => sr.YnabBudgetId == ynabBudgetId && context.Set<TransactionEntity>()
				.Where(t => receiptIds.Contains(t.ReceiptId))
				.Select(t => t.Id)
				.Contains(sr.LocalTransactionId))
			.Include(sr => sr.Transaction)
			.AsNoTracking()
			.ToListAsync(cancellationToken);
	}

	public async Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.Where(e => e.YnabBudgetId == ynabBudgetId && e.SyncStatus == YnabSyncStatus.Synced && e.SyncedAtUtc != null)
			.OrderByDescending(e => e.SyncedAtUtc)
			.Select(e => e.SyncedAtUtc)
			.FirstOrDefaultAsync(cancellationToken);
	}
}
