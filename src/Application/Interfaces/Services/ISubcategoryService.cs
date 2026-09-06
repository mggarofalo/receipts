using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface ISubcategoryService : ISoftDeletableService<Subcategory>
{
	Task<PagedResult<Subcategory>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken);
	Task<List<Subcategory>> CreateAsync(List<Subcategory> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Subcategory> models, CancellationToken cancellationToken);
	Task<PagedResult<Subcategory>> GetByCategoryIdAsync(Guid categoryId, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<PagedResult<Subcategory>> GetByCategoryIdAsync(Guid categoryId, int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken);
	Task<SubcategoryUsage> GetUsageAsync(Guid categoryId, string subcategoryName, int receiptLimit, CancellationToken cancellationToken);
}
