using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabAccountMappingService
{
	Task<List<YnabAccountMappingDto>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabAccountMappingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabAccountMappingDto> CreateAsync(Guid receiptsAccountId, string ynabAccountId, string ynabAccountName, string ynabBudgetId, CancellationToken cancellationToken);
	Task UpdateAsync(Guid id, string ynabAccountId, string ynabAccountName, string ynabBudgetId, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
	Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken);
}
