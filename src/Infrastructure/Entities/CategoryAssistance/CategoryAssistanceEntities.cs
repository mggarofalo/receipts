namespace Infrastructure.Entities.CategoryAssistance;

// Local operational state: deliberately excluded from portable backup DTOs.
public sealed class CategoryAssistanceSettingsEntity
{
	public int Id { get; set; } = 1;
	public bool Enabled { get; set; }
	public string? Provider { get; set; }
	public string? Model { get; set; }
	public int DailyLimit { get; set; } = 10;
	public int PendingLimit { get; set; } = 3;
	public Guid Revision { get; set; }
	public Guid RestoreGeneration { get; set; }
}
public sealed class CategoryAssistanceSettingsAuditEntity
{
	public Guid Id { get; set; }
	public string Actor { get; set; } = "";
	public Guid PreviousRevision { get; set; }
	public Guid Revision { get; set; }
	public string Before { get; set; } = "";
	public string After { get; set; } = "";
	public DateTimeOffset RecordedAt { get; set; }
}
public sealed class CategoryAssistanceObservationEntity
{
	public Guid SaveId { get; set; }
	public Guid ReceiptItemId { get; set; }
	public Guid ReceiptId { get; set; }
	public string Actor { get; set; } = "";
	public string Location { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public string SourceHash { get; set; } = "";
	public Guid ItemRevision { get; set; }
	public Guid ReceiptRevision { get; set; }
	public Guid? AssignmentId { get; set; }
	public Guid SettingsRevision { get; set; }
	public Guid RestoreGeneration { get; set; }
	public Guid TaxonomyRevision { get; set; }
	public string Evidence { get; set; } = "post_consent_no_choice_save";
	public DateTimeOffset RecordedAt { get; set; }
}
