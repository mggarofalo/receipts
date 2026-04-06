using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabCategoryMappingService
{
	Task<List<YnabCategoryMappingDto>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabCategoryMappingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabCategoryMappingDto?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken);
	Task<YnabCategoryMappingDto> CreateAsync(string receiptsCategory, string ynabCategoryId, string ynabCategoryName, string ynabCategoryGroupName, string ynabBudgetId, CancellationToken cancellationToken);
	Task UpdateAsync(Guid id, string ynabCategoryId, string ynabCategoryName, string ynabCategoryGroupName, string ynabBudgetId, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken);
	Task<List<string>> GetUnmappedCategoriesAsync(CancellationToken cancellationToken);
	Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
	Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
}
