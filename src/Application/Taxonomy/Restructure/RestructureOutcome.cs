using System.Text.Json;
using Domain.Taxonomy;

namespace Application.Taxonomy.Restructure;

/// <summary>
/// What a plan does to one deployment. The dry run reports it; apply executes exactly these changes,
/// so the two cannot disagree.
/// </summary>
public sealed record RestructureOutcome(
	IReadOnlyList<string> Errors,
	IReadOnlyList<string> Warnings,
	IReadOnlyList<TaxonomyNode> ResultNodes,
	IReadOnlyList<RestructureItemChange> ItemChanges,
	IReadOnlyList<RestructureDefaultChange> DefaultChanges,
	IReadOnlyList<RestructureMappingChange> MappingChanges,
	IReadOnlyList<RestructureResolvedMerge> EnvelopeMerges,
	IReadOnlyList<string> OccasionsToCreate,
	RestructureReport Report)
{
	public bool IsValid => Errors.Count == 0;
}

/// <summary>One purchase the plan reassigns. A conflict is reported and never written.</summary>
public sealed record RestructureItemChange(Guid ItemId, Guid ExpectedAssignmentId, Guid? PreviousCategoryId,
	Guid? TargetCategoryId, Guid? PreviousOccasionId, string? TargetOccasion, string Rule, string Evidence,
	bool Conflict);

public sealed record RestructureDefaultChange(Guid CategoryId, Guid? PreviousEnvelopeId, Guid? EnvelopeId);

/// <summary>Re-approves (or, with a null category, revokes) an approved mapping at the new revision.</summary>
public sealed record RestructureMappingChange(RestructureMappingState Mapping, Guid? CategoryId);

public sealed record RestructureResolvedMerge(Guid SourceEnvelopeId, Guid TargetEnvelopeId);

/// <summary>Stored as the reason of each <c>restructure</c> assignment.</summary>
public sealed record RestructureEvidence(Guid PlanId, string Rule, Guid? From, JsonElement? Inputs = null);

/// <summary>Counts only; category paths are the deployment's own names, shown to its administrator.</summary>
public sealed record RestructureReport(
	RestructureTaxonomyReport Taxonomy,
	RestructureItemReport Items,
	IReadOnlyList<RestructureTransition> Transitions,
	RestructureDefaultsReport Defaults,
	RestructureMappingsReport Mappings,
	IReadOnlyList<RestructureMergeReport> EnvelopeMerges,
	RestructureBudgetReport Budget,
	IReadOnlyList<RestructureOccasionReport> Occasions);

public sealed record RestructureTaxonomyReport(int Created, int Renamed, int Moved, int Defined, int Archived,
	int Restored, int ActiveBefore, int ActiveAfter);

public sealed record RestructureItemReport(int Considered, int Reassigned, int Cleared, int Unchanged,
	int Conflicts, int MissingFromDeployment, IReadOnlyDictionary<string, int> ByRule);

public sealed record RestructureTransition(Guid? FromCategoryId, string FromPath, Guid? ToCategoryId, string ToPath,
	int Count);

public sealed record RestructureDefaultsReport(int Kept, int Moved, int Removed, int Set);

public sealed record RestructureMappingsReport(int Reapproved, int Repointed, int Revoked, int LeftStale);

public sealed record RestructureMergeReport(string Source, string Target, int Items, int Receipts, int Defaults,
	int ItemsChangingYnabCategory);

/// <summary>Restructure never changes an item's envelope; only envelope merges move purchases between envelopes.</summary>
public sealed record RestructureBudgetReport(int ItemsChangingEnvelope, int ItemsChangingYnabCategory);

public sealed record RestructureOccasionReport(string Name, bool Created, int Items);
