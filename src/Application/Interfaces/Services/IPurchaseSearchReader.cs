using Application.Models.Search;

namespace Application.Interfaces.Services;

/// <summary>Active purchase projections, filtered before counting and pagination.</summary>
public interface IPurchaseSearchReader
{
	Task<PurchaseSearchResult> SearchAsync(string query, int offset, int limit, CancellationToken cancellationToken);
}
