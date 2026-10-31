using Application.Normalization;
using Domain.Taxonomy;

namespace Application.Taxonomy.Restructure;

/// <summary>Everything the planner reads from a deployment, captured under the restructure locks.</summary>
public sealed record RestructureState(
	Guid CurrentRevisionId,
	IReadOnlyList<TaxonomyNode> CurrentNodes,
	IReadOnlyList<TaxonomyNode>? BaseNodes,
	IReadOnlyList<RestructureItemState> Items,
	IReadOnlyList<RestructureEnvelopeState> Envelopes,
	IReadOnlyList<RestructureOccasionState> Occasions,
	IReadOnlyDictionary<Guid, Guid> Defaults,
	IReadOnlyList<RestructureMappingState> Mappings,
	IReadOnlyList<RestructureReceiptState> PaidFromReceipts);

/// <summary>A purchase and its current assignment (null category when cleared).</summary>
public sealed record RestructureItemState(Guid ItemId, Guid AssignmentId, Guid? CategoryId, DateTimeOffset RecordedAt,
	string Source, Guid? OccasionId, Guid? EnvelopeId);

public sealed record RestructureEnvelopeState(Guid Id, string Name, bool IsActive,
	IReadOnlyList<RestructureEnvelopeLink> Links);

public sealed record RestructureEnvelopeLink(string BudgetId, string YnabCategoryId);

public sealed record RestructureOccasionState(Guid Id, string Name, bool IsActive);

/// <summary>The current head of an approved mapping that names a category.</summary>
public sealed record RestructureMappingState(NormalizationSourceKey Source, string SourceFingerprint, Guid RevisionId,
	Guid CategoryId, string? SemanticFingerprint);

public sealed record RestructureReceiptState(Guid ReceiptId, Guid PaidFromEnvelopeId);
