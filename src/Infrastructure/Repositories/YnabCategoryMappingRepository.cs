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

	public async Task UpdateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabCategoryMappingEntity? existing = await context.YnabCategoryMappings.FindAsync([entity.Id], cancellationToken);
		if (existing is not null)
		{
			context.Entry(existing).CurrentValues.SetValues(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabCategoryMappingEntity? entity = await context.YnabCategoryMappings.FindAsync([id], cancellationToken);
		if (entity is not null)
		{
			context.YnabCategoryMappings.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
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
}
