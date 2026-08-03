using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IReceiptService : ISoftDeletableService<Receipt>
{
	Task<PagedResult<Receipt>> GetAllAsync(int offset, int limit, SortParams sort, Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken);
	Task<List<Receipt>> CreateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task UpdateImagePathsAsync(Guid receiptId, string originalImagePath, string processedImagePath, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken);
}
