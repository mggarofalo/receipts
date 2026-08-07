using Application.Models;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IReceiptItemRepository
{
	Task<ReceiptItemEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<List<ReceiptItemEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetByReceiptIdCountAsync(Guid receiptId, CancellationToken cancellationToken);
	Task<List<ReceiptItemEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<List<ReceiptItemEntity>> GetAllAsync(int offset, int limit, SortParams sort, string? q, CancellationToken cancellationToken);

	// normalizedDescriptionId narrows to the items linked to one canonical row (RECEIPTS-877).
	// The split dialog needs every linked item, not whichever of them happen to fall in the most
	// recent page of the unfiltered list.
	Task<List<ReceiptItemEntity>> GetAllAsync(int offset, int limit, SortParams sort, string? q, Guid? normalizedDescriptionId, CancellationToken cancellationToken);
	Task<List<ReceiptItemEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<int> GetDeletedCountAsync(CancellationToken cancellationToken);
	Task<List<ReceiptItemEntity>> CreateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken);
	Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken);
	Task<int> GetCountAsync(string? q, CancellationToken cancellationToken);
	Task<int> GetCountAsync(string? q, Guid? normalizedDescriptionId, CancellationToken cancellationToken);
	Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
	Task<List<ReceiptItemSuggestion>> GetSuggestionsAsync(string itemCode, string? location, int limit, CancellationToken cancellationToken);
}
