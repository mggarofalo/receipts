namespace Infrastructure.Entities.Taxonomy;

/// <summary>
/// One application of a restructure plan. Its purchase reassignments run as the historical operation
/// with the same ID (kind <see cref="HistoricalOperationKind.RestructureV1"/>), whose state is the
/// application's state. Deployment-local: not part of portable backups.
/// </summary>
public sealed class RestructureApplicationEntity
{
	public Guid Id { get; set; }
	public Guid PlanId { get; set; }
	public string Actor { get; set; } = string.Empty;
	public string PlanHash { get; set; } = string.Empty;
	public string PlanJson { get; set; } = string.Empty;
	public string ReportJson { get; set; } = string.Empty;
	public string Reason { get; set; } = string.Empty;
	public Guid BaseRevisionId { get; set; }
	public Guid AppliedRevisionId { get; set; }
	public Guid? UndoRevisionId { get; set; }
	public DateTimeOffset AppliedAt { get; set; }
	public DateTimeOffset? UndoneAt { get; set; }
}

/// <summary>The rule, evidence and occasion tag of one reassigned purchase.</summary>
public sealed class RestructureItemEntity
{
	public Guid ApplicationId { get; set; }
	public Guid ItemId { get; set; }
	public string Rule { get; set; } = string.Empty;
	public string Evidence { get; set; } = string.Empty;
	public Guid? PreviousOccasionId { get; set; }
	public Guid? TargetOccasionId { get; set; }
}

/// <summary>
/// Every change an application makes outside purchase assignments, with its previous value, so undo
/// can put it back. Undo restores a row only while its current value is still the one written here.
/// </summary>
public sealed class RestructureChangeEntity
{
	public Guid ApplicationId { get; set; }
	public int Ordinal { get; set; }
	public RestructureChangeKind Kind { get; set; }
	public Guid SubjectId { get; set; }
	public Guid? Before { get; set; }
	public Guid? After { get; set; }
	public string? UndoOutcome { get; set; }
}

public enum RestructureChangeKind
{
	/// <summary>Subject: item type. Before/after: default envelope.</summary>
	EnvelopeDefault,
	/// <summary>Subject: purchase. Before/after: envelope (an envelope merge).</summary>
	ItemEnvelope,
	/// <summary>Subject: receipt. Before/after: paid-from envelope (an envelope merge).</summary>
	ReceiptPaidFrom,
	/// <summary>Subject: the envelope a merge archived.</summary>
	EnvelopeArchived,
	/// <summary>Subject: an occasion the plan created.</summary>
	OccasionCreated,
	/// <summary>Subject: the mapping revision written. Before/after: category.</summary>
	ApprovedMapping
}
