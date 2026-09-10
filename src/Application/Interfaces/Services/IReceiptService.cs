using Application.Models;
using Application.Models.Images;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IReceiptService : ISoftDeletableService<Receipt>
{
	Task<PagedResult<ReceiptListItem>> GetAllAsync(int offset, int limit, SortParams sort, Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken);
	Task<List<Receipt>> CreateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task<ReceiptImageSet?> ReplaceImagePathsAsync(Guid receiptId, ReceiptImageSet imageSet, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken);
}
