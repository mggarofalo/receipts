namespace Infrastructure.Entities.Taxonomy;

// A row only exists after the entire backfill commits. Backup acceptance is a separate gate.
public sealed class LegacyMigrationEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public string OperationKind { get; set; } = "";
	public string SourceDataset { get; set; } = "";
	public string RequestHash { get; set; } = "";
	public Guid RevisionId { get; set; }
	public DateTimeOffset ObservedAt { get; set; }
	public string PlanJson { get; set; } = "";
}

public sealed class LegacyMigrationCrosswalkEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public string SourceTable { get; set; } = "";
	public Guid SourceId { get; set; }
	public Guid TargetId { get; set; }
	public string EvidenceJson { get; set; } = "";
}

public sealed class LegacyMigrationItemEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public Guid ReceiptItemId { get; set; }
	public Guid AssignmentId { get; set; }
	public string Resolution { get; set; } = "";
	public string EvidenceJson { get; set; } = "";
}
