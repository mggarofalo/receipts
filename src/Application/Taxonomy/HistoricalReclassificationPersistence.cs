namespace Application.Taxonomy;

// Only intent crosses this boundary. The server captures source revisions and previous assignments.
public sealed record HistoricalGroupSelection(HistoricalGroupKey Group, Guid? TargetCategoryId);
public sealed record HistoricalPreviewRequest(Guid OperationId, string Actor, Guid TaxonomyRevisionId,
	string Reason, IReadOnlyList<HistoricalGroupSelection> Groups);
public sealed record HistoricalProgress(Guid OperationId, HistoricalOperationState State, int Total,
	int Applied, int Conflicted, int Skipped, int Pending, int Undone, int UndoConflicted, int UndoSkipped);
public sealed record HistoricalItemPage(IReadOnlyList<HistoricalItemResult> Items,
	IReadOnlyList<HistoricalUndoResult> UndoItems, int? NextOffset);

public interface IHistoricalOperationService
{
	Task<HistoricalProgress> SavePreviewAsync(HistoricalPreviewRequest request, CancellationToken ct = default);
	Task<HistoricalProgress> ReadAsync(string actor, Guid id, CancellationToken ct = default);
	Task<HistoricalItemPage> ReadItemsAsync(string actor, Guid id, int offset, int limit, CancellationToken ct = default);
	Task<HistoricalProgress> ApplyChunkAsync(string actor, Guid id, int limit, CancellationToken ct = default);
	Task<HistoricalProgress> CancelAsync(string actor, Guid id, CancellationToken ct = default);
	Task<HistoricalProgress> UndoChunkAsync(string actor, Guid id, HistoricalUndoCommand command, int limit, CancellationToken ct = default);
}
