namespace Infrastructure.Entities.Organization;

// Deployment-local discovery evidence only. No effects or portable business evidence.
public sealed class OrganizationRunEntity
{
	public Guid Id { get; set; }
	public int FormatVersion { get; set; } = 1;
	public Guid PolicyRevision { get; set; }
	public Guid RestoreGeneration { get; set; }
	public Guid TaxonomyRevision { get; set; }
	public bool Active { get; set; } = true;
	public string State { get; set; } = "scanning";
	public Guid? Cursor { get; set; }
	public Guid? HighWater { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OrganizationSourceEntity
{
	public Guid Id { get; set; }
	public Guid RunId { get; set; }
	public int FormatVersion { get; set; } = 1;
	public string RawHash { get; set; } = "";
	public string Store { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public string NormalizedHash { get; set; } = "";
	public string NormalizedStore { get; set; } = "";
	public string NormalizedDescription { get; set; } = "";
	public string? NormalizedItemCode { get; set; }
}

public sealed class OrganizationItemEntity
{
	public Guid RunId { get; set; }
	public Guid ItemId { get; set; }
	public Guid? SourceId { get; set; }
	public int FormatVersion { get; set; } = 1;
	public Guid ReceiptId { get; set; }
	public Guid ItemRevision { get; set; }
	public Guid ReceiptRevision { get; set; }
	public Guid? ExpectedAssignmentId { get; set; }
	public Guid? PreviousCategoryId { get; set; }
	public string State { get; set; } = "observed";
	public DateTimeOffset ObservedAt { get; set; }
}

// Coalescing transactional inbox. No sequence cursor: a slow commit cannot fall behind it.
public sealed class OrganizationDirtyItemEntity
{
	public Guid ItemId { get; set; }
}

// Exact source invalidation; trigger writes are independent of purchase count.
public sealed class OrganizationDirtySourceEntity
{
	public Guid Id { get; set; }
	public int FormatVersion { get; set; } = 1;
	public string Kind { get; set; } = "raw";
	public string Store { get; set; } = "";
	public string Description { get; set; } = "";
	public string? ItemCode { get; set; }
	public Guid? Cursor { get; set; }
}
