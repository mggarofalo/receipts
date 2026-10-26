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
// Deployment-local transport binding. The fingerprint is an opaque, secret-free digest
// supplied by the selected transport; neither credentials nor endpoints are stored here.
public sealed class CategoryAssistanceConnectionEntity
{
	public int Id { get; set; } = 1;
	public string Provider { get; set; } = "";
	public string Model { get; set; } = "";
	public string ConfigurationFingerprint { get; set; } = "";
	public Guid Generation { get; set; }
	public bool Changing { get; set; } = true;
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
	public Guid? AttemptId { get; set; }
	public bool DispatchSkipped { get; set; }
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

public sealed class CategoryAssistanceAttemptEntity
{
	public Guid Id { get; set; }
	public Guid RequestId { get; set; }
	public Guid RequestScope { get; set; }
	public Guid RestoreGeneration { get; set; }
	public string SourceHash { get; set; } = "";
	public string Location { get; set; } = "";
	public string Description { get; set; } = "";
	public string ItemCodeKey { get; set; } = "";
	public Guid SourceRevision { get; set; }
	public Guid TaxonomyRevision { get; set; }
	public string Provider { get; set; } = "";
	public string Model { get; set; } = "";
	public string QuestionPolicyVersion { get; set; } = "";
	public Guid SettingsRevision { get; set; }
	public Guid ConnectionGeneration { get; set; }
	public Guid ReceiptId { get; set; }
	public Guid ReceiptRevision { get; set; }
	public Guid ItemId { get; set; }
	public Guid ItemRevision { get; set; }
	public Guid? AssignmentId { get; set; }
	public string RequestJson { get; set; } = "";
	public string? ResultJson { get; set; }
	public Guid? DecisionOperationId { get; set; }
	public string? DecisionActor { get; set; }
	public string? DecisionJson { get; set; }
	public string State { get; set; } = "queued";
	public Guid? ClaimToken { get; set; }
	public DateTimeOffset? LeaseExpiresAt { get; set; }
	public DateTimeOffset? NextEligibleAt { get; set; }
	public int Deferrals { get; set; }
	public DateOnly? DebitUtcDay { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CategoryAssistanceDailyUsageEntity
{
	public DateOnly UtcDay { get; set; }
	public int DispatchAttempts { get; set; }
}
