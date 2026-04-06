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

	public async Task<YnabSyncRecordEntity?> GetByTransactionAndTypeAsync(Guid localTransactionId, YnabSyncType syncType, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.FirstOrDefaultAsync(e => e.LocalTransactionId == localTransactionId && e.SyncType == syncType, cancellationToken);
	}

	public async Task UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? existing = await context.YnabSyncRecords.FindAsync([entity.Id], cancellationToken);
		if (existing is not null)
		{
			context.Entry(existing).CurrentValues.SetValues(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
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
}
