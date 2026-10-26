namespace Application.Taxonomy;

// Exact raw evidence: ordinal strings, no trimming, case folding, or inferred product identity.
public sealed record HistoricalGroupKey(string Store, string RawDescription, string? ItemCode);

public sealed record HistoricalReclassificationItem(Guid ItemId, Guid ReceiptId, HistoricalGroupKey Group,
	Guid ItemRevision, Guid ReceiptRevision, Guid? ExpectedAssignmentId, Guid? PreviousCategoryId,
	Guid? TargetCategoryId, Guid ApplyOperationId, Guid UndoOperationId);

// CategorySelectable refers to the proposed category for apply, and the previous category for undo.
public sealed record HistoricalCurrentState(bool Available, HistoricalGroupKey Group, Guid ItemRevision,
	Guid ReceiptRevision, Guid? AssignmentId, Guid TaxonomyRevisionId, bool CategorySelectable);

public enum HistoricalOperationState { Preview, Applying, Completed, Cancelled, Undoing, Undone }
public enum HistoricalItemState { Pending, Applied, Conflicted, Skipped }
public enum HistoricalDecisionKind { Append, Conflict, Skip }
public sealed record HistoricalDecision(HistoricalDecisionKind Kind, string? Reason = null);
public sealed record HistoricalItemResult(HistoricalReclassificationItem Item, HistoricalItemState State,
	Guid? AppliedAssignmentId, string? Reason);
public sealed record HistoricalUndoResult(Guid ItemId, HistoricalItemState State, Guid? UndoAssignmentId, string? Reason);

// The service must persist a defensive copy of this exact selection and evidence before any writes.
public sealed record HistoricalPreview(Guid OperationId, string Actor, Guid TaxonomyRevisionId,
	DateTimeOffset CapturedAt, string Reason, IReadOnlyList<HistoricalGroupKey> SelectedGroups,
	IReadOnlyList<HistoricalReclassificationItem> Items);
public sealed record HistoricalOperation(HistoricalPreview Preview, HistoricalOperationState State,
	IReadOnlyList<HistoricalItemResult> Items, IReadOnlyList<HistoricalUndoResult> UndoItems);
public sealed record HistoricalUndoCommand(Guid TaxonomyRevisionId, string Reason);

// Contract only: no runner is registered until transactional persistence is implemented.
public interface IHistoricalReclassificationService
{
	Task<HistoricalOperation> ReadAsync(string actor, Guid operationId, CancellationToken cancellationToken = default);
	Task<HistoricalOperation> SavePreviewAsync(HistoricalPreview preview, CancellationToken cancellationToken = default);
	Task<HistoricalOperation> ApplyChunkAsync(string actor, Guid operationId, int limit, CancellationToken cancellationToken = default);
	Task<HistoricalOperation> CancelAsync(string actor, Guid operationId, CancellationToken cancellationToken = default);
	Task<HistoricalOperation> UndoChunkAsync(string actor, Guid operationId, HistoricalUndoCommand command, int limit, CancellationToken cancellationToken = default);
}
