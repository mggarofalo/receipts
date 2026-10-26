namespace Application.Taxonomy;

public sealed record HistoricalGroupBrowseRequest(int Offset = 0, int Limit = 25,
	string? Search = null, bool ConflictsOnly = false, bool MigratedBroadOnly = false,
		bool UnresolvedOnly = false, bool UnassignedOnly = false,
		string Actor = "", HistoricalDispositionFilter DispositionFilter = HistoricalDispositionFilter.Visible);
public enum HistoricalDispositionFilter { Visible, All, Later, LeaveAsIs }

public sealed record HistoricalGroupCategory(Guid? CategoryId, string? Path, int PurchaseCount);

public sealed record HistoricalGroupSummary(HistoricalGroupKey Group, int PurchaseCount,
	bool Conflict, bool MigratedBroad, bool Unresolved, bool Unassigned,
		IReadOnlyList<HistoricalGroupCategory> CurrentCategories,
		HistoricalGroupDisposition? Disposition = null, long DispositionVersion = 0);

public sealed record HistoricalGroupPage(IReadOnlyList<HistoricalGroupSummary> Items,
	int TotalGroups, int? NextOffset);

public interface IHistoricalGroupReader
{
	Task<HistoricalGroupPage> BrowseAsync(HistoricalGroupBrowseRequest request,
		CancellationToken cancellationToken = default);
}
