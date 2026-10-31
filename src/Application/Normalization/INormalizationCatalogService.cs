namespace Application.Normalization;

/// <summary>Reviewed source mappings shared by historical cleanup and future receipt resolution.</summary>
public interface INormalizationCatalogService
{
	Task<NormalizationCatalogLabelReview> PreviewLabelAsync(string actor, Guid targetId, string? displayLabel, CancellationToken cancellationToken);
	Task<NormalizationCatalogLabelReview> ApproveLabelAsync(string actor, Guid targetId, NormalizationCatalogLabelApproval request, CancellationToken cancellationToken);
	Task<NormalizationSourcePage> ListSourcesAsync(string actor, string? query, string disposition, int offset, int limit, CancellationToken cancellationToken);
	Task<NormalizationCatalogCoverage> GetCoverageAsync(string actor, CancellationToken cancellationToken);
	Task<IReadOnlyList<NormalizationCatalogCandidate>> GetCandidatesAsync(string actor, NormalizationSourceKey source, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation> PreviewAsync(string actor, NormalizationCatalogPreview request, CancellationToken cancellationToken);
	Task<IReadOnlyList<NormalizationCatalogOperation>> ListOperationsAsync(string actor, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation?> GetOperationAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation> ApproveAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation> ResumeAsync(string actor, Guid id, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation> CancelAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken);
	Task<NormalizationCatalogOperation> UndoAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken);
	Task<NormalizationSourceMapping> RevokeAsync(string actor, Guid id, Guid expectedRevision, CancellationToken cancellationToken);
	Task<IReadOnlyList<NormalizationSourceMapping>> ListMappingsAsync(string actor, NormalizationSourceKey source, CancellationToken cancellationToken);
}

/// <summary>Exact ordinal keys after trimming outer ASCII spaces (U+0020). Missing/space-only codes are distinct from present codes.</summary>
public sealed record NormalizationSourceKey(string Location, string Description, string? ItemCode);
public sealed record NormalizationSourceGroup(NormalizationSourceKey Source, int ItemCount, int UnassignedCount,
	int LegacyUnknownCount, int AutomaticCount, int ProtectedCount, Guid? MappingId, int RevisableCount = 0, IReadOnlyList<NormalizationLinkedTarget>? LinkedTargets = null, int LinkedTargetCount = 0);
public sealed record NormalizationSourcePage(IReadOnlyList<NormalizationSourceGroup> Items, int Total);
public sealed record NormalizationCatalogCoverage(int TotalItems, int Unassigned, int LegacyUnknown,
	int Automatic, int Reviewed, int Mapped, int Protected, int Excluded, int Revisable = 0, int VerifiedItems = 0, int PendingItems = 0, int PendingSources = 0,
	DateTimeOffset? OldestUnresolvedAt = null, int ExcludedItems = 0);
public sealed record NormalizationCatalogPreview(Guid ClientRequestId, NormalizationSourceKey Source,
	Guid? TargetId, Guid? TargetRevision, string? NewCanonicalName, bool IncludeLegacyUnknown, bool Exclude = false,
	Guid? SuggestionRunId = null, Guid? SuggestionProposalId = null, Guid? SuggestionRunRevision = null);
public sealed record NormalizationSourceMapping(Guid Id, Guid Revision, NormalizationSourceKey Source,
	Guid? TargetId, string? TargetName, string State, DateTimeOffset ApprovedAt);
/// <param name="SemanticSimilarity">
/// Cosine similarity to the source when the target is among its nearest semantic neighbours. Retrieval
/// evidence only: never exposed by the API, and not a confidence (docs/receipt-suggestion-flows.md).
/// </param>
public sealed record NormalizationCatalogCandidate(Guid Id, Guid Revision, string CanonicalName, string? DisplayLabel,
	IReadOnlyList<string> Channels, IReadOnlyList<string> ApprovedExamples, double? SemanticSimilarity = null);
public sealed record NormalizationCatalogOperation(Guid Id, Guid Revision, string State,
	NormalizationSourceKey Source, Guid? MappingId, Guid? TargetId, string? NewCanonicalName,
	bool IncludeLegacyUnknown, int Total, int Eligible, int Protected, int Applied, int Conflicted,
	int Remaining, int Undone, DateTimeOffset CreatedAt, string? TargetName, bool Exclude,
	string? ErrorCode = null, string? ErrorDetail = null);

public sealed record NormalizationCatalogLabelReview(Guid TargetId, Guid TargetRevision, string CanonicalName,
	string? CurrentDisplayLabel, string? ProposedDisplayLabel, int AffectedItemCount);
public sealed record NormalizationCatalogLabelApproval(string? DisplayLabel, Guid ExpectedTargetRevision, int ExpectedAffectedItemCount,
	Guid? SuggestionRunId = null, Guid? SuggestionProposalId = null, Guid? SuggestionRunRevision = null);

public sealed record NormalizationLinkedTarget(Guid Id, string Name, Domain.NormalizedDescriptions.NormalizedDescriptionStatus Status);
