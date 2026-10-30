using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Envelopes;

public class EnvelopeEntity : ISoftDeletable
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool IsActive { get; set; }
	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }
	public Guid? CascadeDeletedByParentId { get; set; }
}

public class OccasionEntity : ISoftDeletable
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool IsActive { get; set; }
	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }
	public Guid? CascadeDeletedByParentId { get; set; }
}

/// <summary>Item type (taxonomy identity) → default envelope; resolved by nearest mapped ancestor.</summary>
public class ItemTypeEnvelopeDefaultEntity
{
	public Guid Id { get; set; }
	public Guid CategoryId { get; set; }
	public Guid EnvelopeId { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Envelope → YNAB category, one per envelope and budget.</summary>
public class YnabEnvelopeMappingEntity
{
	public Guid Id { get; set; }
	public Guid EnvelopeId { get; set; }
	public string YnabBudgetId { get; set; } = string.Empty;
	public string YnabCategoryId { get; set; } = string.Empty;
	public string YnabCategoryName { get; set; } = string.Empty;
	public string YnabCategoryGroupName { get; set; } = string.Empty;
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Singleton completion record of the historical envelope backfill.</summary>
public class EnvelopeBackfillEntity
{
	public int Id { get; set; }
	public DateTimeOffset CompletedAt { get; set; }
	public int ItemCount { get; set; }
	public int EnvelopeCount { get; set; }
	public int DefaultCount { get; set; }
}
