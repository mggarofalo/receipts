using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface ICategoryService : ISoftDeletableService<Category>
{
	Task<PagedResult<Category>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken);
	Task<List<Category>> CreateAsync(List<Category> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Category> models, CancellationToken cancellationToken);
	Task<int> GetSubcategoryCountAsync(Guid categoryId, CancellationToken cancellationToken);
	Task<int> GetReceiptItemCountByCategoryNameAsync(string categoryName, CancellationToken cancellationToken);
	Task<List<string>> GetSubcategoryNamesAsync(Guid categoryId, CancellationToken cancellationToken);
	Task<int> GetReceiptItemCountBySubcategoryNamesAsync(List<string> subcategoryNames, CancellationToken cancellationToken);
}
