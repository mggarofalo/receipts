namespace Infrastructure.Entities.NormalizationAi;

/// <summary>Deployment-local consent. Restore/account change disables it; it is never portable.</summary>
public sealed class NormalizationAssistancePreferenceEntity
{
	public int Id { get; set; } = 1;
	public string OwnerUserId { get; set; } = string.Empty;
	public bool Enabled { get; set; }
	public string? Model { get; set; }
	public int DailyLimit { get; set; } = 10;
	public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>One automatic request per owner/source; persisted before admission and never replayed after uncertain inference.</summary>
public sealed class NormalizationAssistanceAttemptEntity
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string OwnerUserId { get; set; } = string.Empty;
	public string SourceHash { get; set; } = string.Empty;
	public string Location { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string? ItemCode { get; set; }
	public Guid Revision { get; set; } = Guid.NewGuid();
	public Guid ClientRequestId { get; set; } = Guid.NewGuid();
	public Guid PreferenceRevision { get; set; }
	public string Model { get; set; } = string.Empty;
	public string State { get; set; } = "pending";
	public Guid? RunId { get; set; }
	public int AdmissionAttempts { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset NextAttemptAt { get; set; }
	public DateTimeOffset? LeaseExpiresAt { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorDetail { get; set; }
}
