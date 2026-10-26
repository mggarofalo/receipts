namespace Application.Taxonomy;

public sealed record HistoricalGroupBrowseRequest(int Offset = 0, int Limit = 25,
	string? Search = null, bool ConflictsOnly = false, bool MigratedBroadOnly = false,
	bool UnresolvedOnly = false, bool UnassignedOnly = false);

public sealed record HistoricalGroupCategory(Guid? CategoryId, string? Path, int PurchaseCount);

public sealed record HistoricalGroupSummary(HistoricalGroupKey Group, int PurchaseCount,
	bool Conflict, bool MigratedBroad, bool Unresolved, bool Unassigned,
	IReadOnlyList<HistoricalGroupCategory> CurrentCategories);

public sealed record HistoricalGroupPage(IReadOnlyList<HistoricalGroupSummary> Items,
	int TotalGroups, int? NextOffset);

public interface IHistoricalGroupReader
{
	Task<HistoricalGroupPage> BrowseAsync(HistoricalGroupBrowseRequest request,
		CancellationToken cancellationToken = default);
}
