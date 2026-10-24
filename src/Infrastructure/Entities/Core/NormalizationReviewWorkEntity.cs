namespace Infrastructure.Entities.Core;

/// <summary>Durable outstanding receipt work; assistance delivery is a separate concern.</summary>
public sealed class NormalizationReviewWorkEntity
{
	public Guid ItemId { get; set; }
	public Guid ReceiptId { get; set; }
	// Null is a dirty database observation awaiting authoritative Application hashing.
	public string? SourceHash { get; set; }
	public Guid SourceRevision { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}
