namespace Infrastructure.Entities.Taxonomy;

// Inert foundation: no production writer or consumer exists. All IDs are portable;
// deployment-local transaction stamps belong to EF shadow properties only.
public sealed class OrganizationEffectManifestEntity
{
	public Guid Id { get; init; }
	public string Actor { get; init; } = "";
	public string RequestHash { get; init; } = "";
	public string PolicyVersion { get; init; } = "";
	public Guid TaxonomyRevisionId { get; init; }
	public DateTimeOffset CapturedAt { get; init; }
}

public sealed class OrganizationEffectItemEntity
{
	public Guid ManifestId { get; init; }
	public Guid ItemId { get; init; }
	public Guid ReceiptId { get; init; }
	public string Store { get; init; } = "";
	public string RawDescription { get; init; } = "";
	public string? ItemCode { get; init; }
	public Guid ItemRevision { get; init; }
	public Guid ReceiptRevision { get; init; }
	public Guid? ExpectedAssignmentId { get; init; }
	public Guid TaxonomyRevisionId { get; init; }
	public Guid TargetCategoryId { get; init; }
	// Stable IDs reserved once, then reused by every retry.
	public Guid ApplyOperationId { get; init; }
	public Guid UndoOperationId { get; init; }
	public string DecisionEvidenceJson { get; init; } = "{}";
}

// Append one terminal result per phase. Pending work is absence of a result.
// Apply and undo are distinct records; an undo never overwrites its apply evidence.
public sealed class OrganizationEffectOutcomeEntity
{
	public Guid ManifestId { get; init; }
	public Guid ItemId { get; init; }
	public string Phase { get; init; } = "apply";
	public string Disposition { get; init; } = "skipped";
	public string Reason { get; init; } = "";
	public Guid? AssignmentId { get; init; }
	public Guid? ItemRevision { get; init; }
	public Guid? ReceiptRevision { get; init; }
	public DateTimeOffset RecordedAt { get; init; }
}

// PostgreSQL populates these atomically when inserting an item. One shared key
// space makes cross-phase reservations safe even under Repeatable Read.
public sealed class OrganizationEffectOperationEntity
{
	public Guid OperationId { get; init; }
	public Guid ManifestId { get; init; }
	public Guid ItemId { get; init; }
	public string Phase { get; init; } = "";
}
