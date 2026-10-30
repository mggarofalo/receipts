using Application.Interfaces;

namespace Application.Queries.Core.Envelope;

public record GetEnvelopeByIdQuery(Guid Id) : IQuery<Domain.Envelopes.Envelope?>;
