using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabAccountMappingRepository
{
	Task<List<YnabAccountMappingEntity>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabAccountMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabAccountMappingEntity> CreateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken);
	Task<bool> UpdateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> CountByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken);
	Task<int> DeleteByBudgetIdNotAsync(string currentBudgetId, CancellationToken cancellationToken);
}
