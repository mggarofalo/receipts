using Application.Taxonomy;

namespace Infrastructure.Entities.Taxonomy;

public enum HistoricalOperationKind { ManualV1, ApprovedMappingV1 }

public sealed class HistoricalOperationEntity
{
	public Guid Id { get; set; }
	public HistoricalOperationKind Kind { get; set; } = HistoricalOperationKind.ManualV1;
	public string Actor { get; set; } = string.Empty;
	public string RequestHash { get; set; } = string.Empty;
	public string SelectionJson { get; set; } = string.Empty;
	public Guid TaxonomyRevisionId { get; set; }
	public DateTimeOffset CapturedAt { get; set; }
	public string Reason { get; set; } = string.Empty;
	public HistoricalOperationState State { get; set; }
	public Guid? UndoTaxonomyRevisionId { get; set; }
	public string? UndoReason { get; set; }
}

public sealed class HistoricalOperationItemEntity
{
	public Guid OperationId { get; set; }
	public Guid ItemId { get; set; }
	public Guid ReceiptId { get; set; }
	public Guid? ApprovedMappingRevisionId { get; set; }
	public string Store { get; set; } = string.Empty;
	public string RawDescription { get; set; } = string.Empty;
	public string? ItemCode { get; set; }
	public Guid ItemRevision { get; set; }
	public Guid ReceiptRevision { get; set; }
	public Guid? ExpectedAssignmentId { get; set; }
	public Guid? PreviousCategoryId { get; set; }
	public Guid? TargetCategoryId { get; set; }
	public Guid ApplyOperationId { get; set; }
	public Guid UndoOperationId { get; set; }
	public HistoricalItemState State { get; set; }
	public Guid? AppliedAssignmentId { get; set; }
	public string? Reason { get; set; }
	public HistoricalItemState UndoState { get; set; }
	public Guid? UndoAssignmentId { get; set; }
	public string? UndoReason { get; set; }

	public HistoricalReclassificationItem Evidence() => new(ItemId, ReceiptId,
		new(Store, RawDescription, ItemCode), ItemRevision, ReceiptRevision, ExpectedAssignmentId,
		PreviousCategoryId, TargetCategoryId, ApplyOperationId, UndoOperationId);
}

// Operational, actor-private review state. It does not represent a classification or verification.
public sealed class HistoricalGroupDispositionEntity
{
	public Guid Id { get; set; }
	public string Actor { get; set; } = string.Empty;
	public string SourceHash { get; set; } = string.Empty;
	public string Store { get; set; } = string.Empty;
	public string RawDescription { get; set; } = string.Empty;
	public string? ItemCode { get; set; }
	public HistoricalGroupDisposition? Disposition { get; set; }
	public long Version { get; set; }
	public Guid OperationId { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}

// Durable idempotence for operational, actor-private review state; excluded from portable taxonomy.
public sealed class HistoricalDispositionOperationEntity
{
	public Guid OperationId { get; set; }
	public string Actor { get; set; } = string.Empty;
	public string SourceHash { get; set; } = string.Empty;
	public string Store { get; set; } = string.Empty;
	public string RawDescription { get; set; } = string.Empty;
	public string? ItemCode { get; set; }
	public HistoricalGroupDisposition? Disposition { get; set; }
	public long ExpectedVersion { get; set; }
	public long ResultVersion { get; set; }
	public DateTimeOffset ResultUpdatedAt { get; set; }
}
