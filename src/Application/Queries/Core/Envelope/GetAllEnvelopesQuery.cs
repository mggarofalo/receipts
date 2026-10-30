using Application.Interfaces;

namespace Application.Queries.Core.Envelope;

public record GetAllEnvelopesQuery(bool? IsActive = null) : IQuery<List<Domain.Envelopes.Envelope>>;
