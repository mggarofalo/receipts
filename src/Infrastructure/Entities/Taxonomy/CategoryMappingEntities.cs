namespace Infrastructure.Entities.Taxonomy;

public sealed class CategoryMappingHeadEntity
{
	public string SourceFingerprint { get; set; } = "";
	public Guid RevisionId { get; set; }
}

// Append-only approval/revocation evidence; operation identity reproduces its original result.
public sealed class CategoryMappingRevisionEntity
{
	public Guid Id { get; set; }
	public Guid? PreviousRevisionId { get; set; }
	public string SourceFingerprint { get; set; } = "";
	public string Location { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public Guid? CategoryId { get; set; }
	public Guid TaxonomyRevisionId { get; set; }
	public string? SemanticFingerprint { get; set; }
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public string RequestHash { get; set; } = "";
	public string Reason { get; set; } = "";
	public DateTimeOffset RecordedAt { get; set; }
}
