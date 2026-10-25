using Domain.Taxonomy;

namespace Application.Taxonomy;

public sealed record LegacyDeletionState(DateTimeOffset? DeletedAt = null, string? DeletedBy = null, Guid? CascadeDeletedByParentId = null,
	Guid? DeletedByApiKeyId = null);
public sealed record LegacyCategoryEvidence(Guid Id, string? Name, string? Definition, bool IsActive, LegacyDeletionState Deletion);
public sealed record LegacySubcategoryEvidence(Guid Id, Guid CategoryId, string? Name, string? Definition, bool IsActive, LegacyDeletionState Deletion);
public sealed record LegacyItemEvidence(Guid Id, Guid ReceiptId, string? Category, string? Subcategory,
	LegacyDeletionState Deletion, LegacyDeletionState ReceiptDeletion, decimal Amount, string Currency,
	Guid? ProductId = null, string? ProductLinkException = null);

// SourceDataset identifies the frozen source, not the destination database. ObservedAt is
// captured once by the caller and reused on retry; it is never a receipt purchase date.
public sealed record LegacyMigrationRequest(string SourceDataset, Guid OperationId, string OperationKind,
	string Actor, DateTimeOffset ObservedAt, IReadOnlyList<LegacyCategoryEvidence> Categories,
	IReadOnlyList<LegacySubcategoryEvidence> Subcategories, IReadOnlyList<LegacyItemEvidence> Items);

public enum LegacyTaxonomySource { Category, Subcategory }
public enum LegacyResolutionKind { ResolvedSuggestion, ResolvedObservedPath, Unclassified, Unresolved }

// A crosswalk exists even for invalid sources that cannot enter a valid snapshot.
public sealed record LegacyTaxonomyCrosswalk(LegacyTaxonomySource Source, Guid SourceId, Guid TargetId,
	LegacyCategoryEvidence? Category, LegacySubcategoryEvidence? Subcategory, string? Exception);
public sealed record LegacyPlannedNode(TaxonomyNode Node, bool ObservedOnly, IReadOnlyList<Guid> ObservedItemIds);
public sealed record LegacyItemResolution(Guid AssignmentId, LegacyItemEvidence Evidence,
	Guid? CategoryId, LegacyResolutionKind Kind, string? Exception);
public sealed record LegacyMigrationException(string Code, bool BlocksCommit, LegacyTaxonomySource? Source = null,
	Guid? SourceId = null, Guid? ItemId = null);
public sealed record LegacyCurrencyTotal(string Currency, decimal Amount);
public sealed record LegacyMigrationCounts(int Categories, int Subcategories, int Items, int TrashedSuggestions,
	int TrashedItems, int Resolved, int Unclassified, int Unresolved, int DuplicatePaths, int DirtyProductLinks,
	IReadOnlyList<LegacyCurrencyTotal> Totals);
public sealed record LegacyMigrationPlan(string SourceDataset, Guid OperationId, string OperationKind, string Actor,
	DateTimeOffset ObservedAt, string RequestHash, Guid RevisionId, IReadOnlyList<LegacyPlannedNode> Nodes,
	IReadOnlyList<LegacyTaxonomyCrosswalk> Crosswalks, IReadOnlyList<LegacyItemResolution> Items,
	IReadOnlyList<LegacyMigrationException> Exceptions, LegacyMigrationCounts Counts)
{
	public bool CanCommit => !Exceptions.Any(exception => exception.BlocksCommit);
}

public sealed class LegacyMigrationReplayException() : Exception("The migration operation was reused with different source evidence or provenance.");
