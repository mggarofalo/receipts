namespace Infrastructure.Entities.Core;

public sealed class NormalizationSourceMappingEntity
{
	public Guid Id { get; set; }
	public Guid Revision { get; set; } = Guid.NewGuid();
	public string SourceHash { get; set; } = "";
	public string Location { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public Guid? TargetId { get; set; }
	public string State { get; set; } = "active";
	public bool Exclude { get; set; }
	public string ApprovedBy { get; set; } = "";
	public DateTimeOffset ApprovedAt { get; set; }
}

public sealed class NormalizationCatalogOperationEntity
{
	public Guid Id { get; set; }
	public Guid Revision { get; set; } = Guid.NewGuid();
	public Guid ClientRequestId { get; set; }
	public string Actor { get; set; } = "";
	public string RequestHash { get; set; } = "";
	public string State { get; set; } = "preview";
	public string? ErrorCode { get; set; }
	public string? ErrorDetail { get; set; }
	public string Location { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public Guid? TargetId { get; set; }
	public Guid? TargetRevision { get; set; }
	public string? NewCanonicalName { get; set; }
	public Guid? MappingId { get; set; }
	public Guid? SuggestionRunId { get; set; }
	public Guid? SuggestionProposalId { get; set; }
	public Guid? SuggestionRunRevision { get; set; }
	public bool IncludeLegacyUnknown { get; set; }
	public bool Exclude { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public List<NormalizationCatalogMemberEntity> Members { get; set; } = [];
}

public sealed class NormalizationCatalogMemberEntity
{
	public Guid Id { get; set; }
	public Guid OperationId { get; set; }
	public Guid ItemId { get; set; }
	public Guid ReceiptId { get; set; }
	public Guid SourceRevision { get; set; }
	public Guid ReceiptRevision { get; set; }
	public Guid? PreviousTargetId { get; set; }
	public string PreviousOrigin { get; set; } = "unassigned";
	public Guid? PreviousMappingId { get; set; }
	public Guid? PreviousAssignmentId { get; set; }
	public double? PreviousScore { get; set; }
	public string State { get; set; } = "pending";
	public Guid? AppliedRevision { get; set; }
	public Guid? AssignmentId { get; set; }
}
