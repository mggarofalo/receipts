using Application.Models;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IReceiptItemService : ISoftDeletableService<ReceiptItem>
{
	Task<PagedResult<ReceiptItem>> GetAllAsync(int offset, int limit, SortParams sort, string? q, CancellationToken cancellationToken);

	// normalizedDescriptionId narrows to the items linked to one canonical row (RECEIPTS-877).
	Task<PagedResult<ReceiptItem>> GetAllAsync(int offset, int limit, SortParams sort, string? q, Guid? normalizedDescriptionId, CancellationToken cancellationToken);
	Task<PagedResult<ReceiptItem>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
	Task<List<ReceiptItem>> CreateAsync(List<ReceiptItem> models, Guid receiptId, CancellationToken cancellationToken);
	Task UpdateAsync(List<ReceiptItem> models, Guid receiptId, CancellationToken cancellationToken);
	Task<List<ReceiptItemSuggestion>> GetSuggestionsAsync(string itemCode, string? location, int limit, CancellationToken cancellationToken);
}
