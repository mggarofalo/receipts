using API.Generated.Dtos;
using Domain.Envelopes;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class EnvelopeMapper
{
	[MapperIgnoreTarget(nameof(EnvelopeResponse.AdditionalProperties))]
	public partial EnvelopeResponse ToResponse(Envelope source);

	[MapperIgnoreTarget(nameof(OccasionResponse.AdditionalProperties))]
	public partial OccasionResponse ToResponse(Occasion source);

	public Envelope ToDomain(CreateEnvelopeRequest source) => new(Guid.Empty, source.Name, source.IsActive);

	public Envelope ToDomain(Guid id, UpdateEnvelopeRequest source) => new(id, source.Name, source.IsActive);

	public Occasion ToDomain(CreateOccasionRequest source) => new(Guid.Empty, source.Name, source.IsActive);

	public Occasion ToDomain(Guid id, UpdateOccasionRequest source) => new(id, source.Name, source.IsActive);
}
