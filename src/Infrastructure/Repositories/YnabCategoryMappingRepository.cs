using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabCategoryMappingRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabCategoryMappingRepository
{
	public async Task<List<YnabCategoryMappingEntity>> GetAllAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabCategoryMappings
			.OrderBy(e => e.ReceiptsCategory)
			.ToListAsync(cancellationToken);
	}

	public async Task<YnabCategoryMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabCategoryMappings.FindAsync([id], cancellationToken);
	}

	public async Task<YnabCategoryMappingEntity?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabCategoryMappings
			.FirstOrDefaultAsync(e => e.ReceiptsCategory == receiptsCategory, cancellationToken);
	}

	public async Task<YnabCategoryMappingEntity> CreateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.YnabCategoryMappings.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<bool> UpdateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabCategoryMappingEntity? existing = await context.YnabCategoryMappings.FindAsync([entity.Id], cancellationToken);
		if (existing is null)
		{
			return false;
		}

		context.Entry(existing).CurrentValues.SetValues(entity);
		return await context.SaveChangesAsync(cancellationToken) > 0;
	}

	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabCategoryMappingEntity? entity = await context.YnabCategoryMappings.FindAsync([id], cancellationToken);
		if (entity is null)
		{
			return false;
		}

		context.YnabCategoryMappings.Remove(entity);
		return await context.SaveChangesAsync(cancellationToken) > 0;
	}

	public async Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.Where(e => e.Category != "")
			.Select(e => e.Category)
			.Distinct()
			.OrderBy(c => c)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> CountByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabCategoryMappings
			.CountAsync(e => e.YnabBudgetId != currentBudgetId, cancellationToken);
	}

	public async Task<int> DeleteByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabCategoryMappings
			.Where(e => e.YnabBudgetId != currentBudgetId)
			.ExecuteDeleteAsync(cancellationToken);
	}
}
