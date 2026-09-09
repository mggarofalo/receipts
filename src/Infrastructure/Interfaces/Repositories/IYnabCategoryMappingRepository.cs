using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabCategoryMappingRepository
{
	Task<List<YnabCategoryMappingEntity>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity> CreateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken);
	Task<bool> UpdateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken);
	Task<int> CountByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken);
	Task<int> DeleteByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken);
}
