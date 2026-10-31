namespace Application.Taxonomy.Restructure;

/// <summary>Validation and the dry run of a plan against this deployment, without changing anything.</summary>
public sealed record RestructurePreview(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
	RestructureReport Report);

/// <summary>One application of a plan. Its state and counts are those of its purchase reassignments.</summary>
public sealed record RestructureApplicationSummary(Guid Id, Guid PlanId, string Reason, DateTimeOffset AppliedAt,
	DateTimeOffset? UndoneAt, HistoricalOperationState State, HistoricalProgress Progress, RestructureReport Report);

public sealed record RestructureApplicationPage(IReadOnlyList<RestructureApplicationSummary> Items, int? NextOffset);

/// <summary>A reassigned purchase as captured, with its outcome. Paths are from before and after the restructure.</summary>
public sealed record RestructureItemRow(Guid ItemId, Guid ReceiptId, string Store, string Description, string FromPath,
	string ToPath, string Rule, HistoricalItemState State, string? Reason, HistoricalItemState UndoState, string? UndoReason);

public sealed record RestructureItemPage(IReadOnlyList<RestructureItemRow> Items, int? NextOffset);

public enum RestructureItemFilter { All, Conflicted, Cleared }

public interface IRestructurePlanService
{
	Task<RestructurePreview> PreviewAsync(RestructurePlan plan, CancellationToken ct = default);

	/// <summary>
	/// Applies the category changes, defaults, envelope merges and approved-mapping re-pointing, and captures every
	/// purchase reassignment for <see cref="ApplyChunkAsync"/>. Replays for the same application ID and plan.
	/// </summary>
	Task<RestructureApplicationSummary> BeginAsync(string actor, Guid applicationId, RestructurePlan plan, CancellationToken ct = default);

	Task<RestructureApplicationSummary> ApplyChunkAsync(string actor, Guid applicationId, int limit, CancellationToken ct = default);

	Task<RestructureApplicationSummary> CancelAsync(string actor, Guid applicationId, CancellationToken ct = default);

	/// <summary>
	/// The first call puts the categories, defaults, envelopes and mappings back; every call then restores up to
	/// <paramref name="limit"/> purchases. Repeat until the state is undone.
	/// </summary>
	Task<RestructureApplicationSummary> UndoChunkAsync(string actor, Guid applicationId, int limit, CancellationToken ct = default);

	Task<RestructureApplicationPage> ListAsync(int offset, int limit, CancellationToken ct = default);

	Task<RestructureApplicationSummary> ReadAsync(Guid applicationId, CancellationToken ct = default);

	Task<RestructureItemPage> ReadItemsAsync(Guid applicationId, RestructureItemFilter filter, int offset, int limit,
		CancellationToken ct = default);
}
