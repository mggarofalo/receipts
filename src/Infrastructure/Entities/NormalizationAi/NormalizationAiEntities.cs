namespace Infrastructure.Entities.NormalizationAi;

public sealed class NormalizationAiConnectionEntity
{
	public int Id { get; set; } = 1;
	public bool Changing { get; set; }
	public Guid Generation { get; set; } = Guid.NewGuid();
}

public sealed class NormalizationAiRunEntity
{
	public Guid Id { get; set; }
	public Guid Revision { get; set; } = Guid.NewGuid();
	public Guid ClientRequestId { get; set; }
	public string OwnerUserId { get; set; } = string.Empty;
	public Guid ConnectionGeneration { get; set; }
	public string RequestHash { get; set; } = string.Empty;
	public string Model { get; set; } = string.Empty;
	public string State { get; set; } = "queued";
	public string PromptVersion { get; set; } = "receipts-normalization-v1";
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }
	public Guid? ClaimToken { get; set; }
	public DateTimeOffset? LeaseExpiresAt { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorDetail { get; set; }
	public string? UsageJson { get; set; }
	public List<NormalizationAiProposalEntity> Proposals { get; set; } = [];
}

public sealed class NormalizationAiProposalEntity
{
	public Guid Id { get; set; }
	public Guid RunId { get; set; }
	public Guid ReceiptItemId { get; set; }
	public Guid ReceiptId { get; set; }
	public Guid SourceRevision { get; set; }
	public string? SourceLocation { get; set; }
	public string? SourceItemCode { get; set; }
	public Guid? SourceReceiptRevision { get; set; }
	public bool IncludeLegacyUnknown { get; set; }
	public bool LabelOnly { get; set; }
	public string? SuggestedDisplayLabel { get; set; }
	public Guid? SourceCanonicalId { get; set; }
	public Guid? SourceCanonicalRevision { get; set; }
	public string Description { get; set; } = string.Empty;
	public string CandidatesJson { get; set; } = "[]";
	public string Action { get; set; } = "pending";
	public Guid? TargetId { get; set; }
	public Guid? TargetRevision { get; set; }
	public double? MatchScore { get; set; }
	public string? CanonicalName { get; set; }
	public string? DisplayLabel { get; set; }
	public string? Reason { get; set; }
	public string State { get; set; } = "pending";
}
