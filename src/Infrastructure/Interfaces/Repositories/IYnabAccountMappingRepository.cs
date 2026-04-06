using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabAccountMappingRepository
{
	Task<List<YnabAccountMappingEntity>> GetAllAsync(CancellationToken cancellationToken);
	Task<YnabAccountMappingEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabAccountMappingEntity> CreateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken);
	Task UpdateAsync(YnabAccountMappingEntity entity, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
