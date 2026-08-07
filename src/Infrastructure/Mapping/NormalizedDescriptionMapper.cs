using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Riok.Mapperly.Abstractions;

namespace Infrastructure.Mapping;

[Mapper]
public partial class NormalizedDescriptionMapper
{
	[MapperIgnoreTarget(nameof(NormalizedDescriptionEntity.Embedding))]
	[MapperIgnoreTarget(nameof(NormalizedDescriptionEntity.EmbeddingModelVersion))]
	[MapperIgnoreTarget(nameof(NormalizedDescriptionEntity.NearestNeighbour))]
	// Derived from DisplayLabel and CanonicalName, both of which map directly. There is no column
	// behind it and persisting one would let the stored copy drift from the rule that computes it.
	[MapperIgnoreSource(nameof(NormalizedDescription.DisplayName))]
	public partial NormalizedDescriptionEntity ToEntity(NormalizedDescription source);

	[MapperIgnoreSource(nameof(NormalizedDescriptionEntity.Embedding))]
	[MapperIgnoreSource(nameof(NormalizedDescriptionEntity.EmbeddingModelVersion))]
	// The navigation carries the same information as NearestNeighbourId, which maps directly.
	// Mapping the object graph too would drag a second entity into the domain model for no gain.
	[MapperIgnoreSource(nameof(NormalizedDescriptionEntity.NearestNeighbour))]
	[MapperIgnoreTarget(nameof(NormalizedDescription.DisplayName))]
	public partial NormalizedDescription ToDomain(NormalizedDescriptionEntity source);
}
