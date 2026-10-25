namespace Infrastructure.Entities.Taxonomy;

public sealed class TaxonomyIdentityEntity { public Guid Id { get; set; } }
public sealed class TaxonomyHeadEntity { public int Id { get; set; } public Guid RevisionId { get; set; } }
public sealed class TaxonomyRevisionEntity
{
	public Guid Id { get; set; }
	public long Sequence { get; set; }
	public Guid? PreviousRevisionId { get; set; }
	public DateTimeOffset RecordedAt { get; set; }
	public string Actor { get; set; } = "";
	public string Reason { get; set; } = "";
	public Guid OperationId { get; set; }
	public int AffectedCount { get; set; }
}
public sealed class TaxonomyNodeEntity
{
	public Guid RevisionId { get; set; }
	public Guid CategoryId { get; set; }
	public Guid? ParentId { get; set; }
	public string Name { get; set; } = "";
	public string? Definition { get; set; }
	public bool Archived { get; set; }
}
public sealed class TaxonomyOperationEntity
{
	public string Actor { get; set; } = "";
	public Guid OperationId { get; set; }
	public string RequestHash { get; set; } = "";
	public Guid RevisionId { get; set; }
	public int AffectedCount { get; set; }
	public DateTimeOffset RecordedAt { get; set; }
	public string Reason { get; set; } = "";
}
