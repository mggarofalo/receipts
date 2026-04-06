using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabCategoryMappingRepository
{
	Task<List<YnabCategoryMappingEntity>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken);
	Task<YnabCategoryMappingEntity> CreateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken);
	Task UpdateAsync(YnabCategoryMappingEntity entity, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken);
}
