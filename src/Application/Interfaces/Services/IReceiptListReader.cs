using Application.Models;

namespace Application.Interfaces.Services;

/// <summary>
/// Reads the receipt-list projection required by the receipt browsing use case.
/// Implementations must not hydrate entity-shaped receipt graphs for this projection.
/// </summary>
public interface IReceiptListReader
{
	Task<PagedResult<ReceiptListItem>> GetAsync(
		int offset,
		int limit,
		SortParams sort,
		Guid? accountId,
		Guid? cardId,
		string? query,
		string? location,
		CancellationToken cancellationToken);
}
