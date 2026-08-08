using Application.Models;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IItemTemplateRepository
{
	Task<ItemTemplateEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<List<ItemTemplateEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	// RECEIPTS-930. Name search, so a picker can search the whole table rather than filtering the
	// page it happened to load. A null or blank term is the unfiltered list.
	Task<List<ItemTemplateEntity>> SearchAsync(string? q, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetCountAsync(string? q, CancellationToken cancellationToken);
	Task<List<ItemTemplateEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetDeletedCountAsync(CancellationToken cancellationToken);
	Task<List<ItemTemplateEntity>> CreateAsync(List<ItemTemplateEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<ItemTemplateEntity> entities, CancellationToken cancellationToken);
	Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken);
	Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
	Task<string?> GetRestoreConflictNameAsync(Guid id, CancellationToken cancellationToken);
}
