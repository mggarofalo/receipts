using Application.Models;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IReceiptRepository
{
	Task<ReceiptEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<List<ReceiptEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<List<ReceiptEntity>> GetAllAsync(int offset, int limit, SortParams sort, Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken);
	Task<List<ReceiptEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetDeletedCountAsync(CancellationToken cancellationToken);
	Task<List<ReceiptEntity>> CreateAsync(List<ReceiptEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<ReceiptEntity> entities, CancellationToken cancellationToken);
	Task UpdateImagePathsAsync(Guid id, string originalImagePath, string processedImagePath, CancellationToken cancellationToken);
	Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken);
	Task<int> GetCountAsync(Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken);
	Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken);
}
