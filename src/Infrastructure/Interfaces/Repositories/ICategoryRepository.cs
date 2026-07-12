using Application.Models;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface ICategoryRepository
{
	Task<CategoryEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<List<CategoryEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null);
	Task<List<CategoryEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetDeletedCountAsync(CancellationToken cancellationToken);
	Task<List<CategoryEntity>> CreateAsync(List<CategoryEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<CategoryEntity> entities, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null);
	Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken);
	Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
	Task<string?> GetRestoreConflictNameAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetSubcategoryCountAsync(Guid categoryId, CancellationToken cancellationToken);
	Task<int> GetReceiptItemCountByCategoryNameAsync(string categoryName, CancellationToken cancellationToken);
	Task<List<string>> GetSubcategoryNamesAsync(Guid categoryId, CancellationToken cancellationToken);
	Task<int> GetReceiptItemCountBySubcategoryNamesAsync(List<string> subcategoryNames, CancellationToken cancellationToken);
}
