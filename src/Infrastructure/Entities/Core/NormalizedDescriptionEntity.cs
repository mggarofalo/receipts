using Domain.NormalizedDescriptions;
using Pgvector;

namespace Infrastructure.Entities.Core;

public class NormalizedDescriptionEntity
{
	public Guid Id { get; set; }
	public string CanonicalName { get; set; } = string.Empty;

	// Optional human-chosen name (RECEIPTS-876). Null means nobody has renamed this row, and
	// readers fall back to CanonicalName. Kept separate from CanonicalName so a rename never
	// touches the text the embedding is anchored to.
	public string? DisplayLabel { get; set; }

	public NormalizedDescriptionStatus Status { get; set; }
	public Vector? Embedding { get; set; }
	public string? EmbeddingModelVersion { get; set; }
	public DateTimeOffset CreatedAt { get; set; }

	// Self-referencing near-miss: the canonical row this entry was closest to when the resolver
	// decided it was too different to auto-accept but too similar to create outright (RECEIPTS-873).
	// Populated only on the PendingReview path; null everywhere else.
	public Guid? NearestNeighbourId { get; set; }
	public double? NearestNeighbourSimilarity { get; set; }
	public virtual NormalizedDescriptionEntity? NearestNeighbour { get; set; }
}
