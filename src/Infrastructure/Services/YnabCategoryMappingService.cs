using Application.Interfaces.Services;
using Application.Models.Ynab;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabCategoryMappingService(IYnabCategoryMappingRepository repository) : IYnabCategoryMappingService
{
	public async Task<List<YnabCategoryMappingDto>> GetAllAsync(CancellationToken cancellationToken)
	{
		List<YnabCategoryMappingEntity> entities = await repository.GetAllAsync(cancellationToken);
		return entities.Select(ToDto).ToList();
	}

	public async Task<YnabCategoryMappingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task<YnabCategoryMappingDto?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByReceiptsCategoryAsync(receiptsCategory, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task<YnabCategoryMappingDto> CreateAsync(
		string receiptsCategory,
		string ynabCategoryId,
		string ynabCategoryName,
		string ynabCategoryGroupName,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabCategoryMappingEntity entity = new()
		{
			ReceiptsCategory = receiptsCategory,
			YnabCategoryId = ynabCategoryId,
			YnabCategoryName = ynabCategoryName,
			YnabCategoryGroupName = ynabCategoryGroupName,
			YnabBudgetId = ynabBudgetId,
			CreatedAt = now,
			UpdatedAt = now,
		};

		YnabCategoryMappingEntity created = await repository.CreateAsync(entity, cancellationToken);
		return ToDto(created);
	}

	public async Task UpdateAsync(
		Guid id,
		string ynabCategoryId,
		string ynabCategoryName,
		string ynabCategoryGroupName,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		if (entity is null)
		{
			return;
		}

		entity.YnabCategoryId = ynabCategoryId;
		entity.YnabCategoryName = ynabCategoryName;
		entity.YnabCategoryGroupName = ynabCategoryGroupName;
		entity.YnabBudgetId = ynabBudgetId;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		await repository.UpdateAsync(entity, cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(id, cancellationToken);
	}

	public async Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken)
	{
		return await repository.GetDistinctReceiptItemCategoriesAsync(cancellationToken);
	}

	public async Task<List<string>> GetUnmappedCategoriesAsync(CancellationToken cancellationToken)
	{
		List<string> allCategories = await repository.GetDistinctReceiptItemCategoriesAsync(cancellationToken);
		List<YnabCategoryMappingEntity> mappings = await repository.GetAllAsync(cancellationToken);

		HashSet<string> mappedCategories = new(mappings.Select(m => m.ReceiptsCategory), StringComparer.Ordinal);

		return allCategories
			.Where(c => !mappedCategories.Contains(c))
			.ToList();
	}

	private static YnabCategoryMappingDto ToDto(YnabCategoryMappingEntity entity) => new(
		entity.Id,
		entity.ReceiptsCategory,
		entity.YnabCategoryId,
		entity.YnabCategoryName,
		entity.YnabCategoryGroupName,
		entity.YnabBudgetId,
		entity.CreatedAt,
		entity.UpdatedAt);
}
