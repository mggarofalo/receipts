using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabAccountMappingRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabAccountMappingRepository
{
	public async Task<List<YnabAccountMappingEntity>> GetAllAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabAccountMappings
			.OrderBy(e => e.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<YnabAccountMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabAccountMappings.FindAsync([id], cancellationToken);
	}

	public async Task<YnabAccountMappingEntity> CreateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.YnabAccountMappings.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task UpdateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabAccountMappingEntity? existing = await context.YnabAccountMappings.FindAsync([entity.Id], cancellationToken);
		if (existing is not null)
		{
			context.Entry(existing).CurrentValues.SetValues(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabAccountMappingEntity? entity = await context.YnabAccountMappings.FindAsync([id], cancellationToken);
		if (entity is not null)
		{
			context.YnabAccountMappings.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<int> CountByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabAccountMappings
			.CountAsync(e => e.YnabBudgetId != currentBudgetId, cancellationToken);
	}

	public async Task<int> DeleteByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabAccountMappings
			.Where(e => e.YnabBudgetId != currentBudgetId)
			.ExecuteDeleteAsync(cancellationToken);
	}
}
