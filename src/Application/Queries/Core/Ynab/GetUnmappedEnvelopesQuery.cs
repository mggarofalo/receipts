using Application.Interfaces;

namespace Application.Queries.Core.Ynab;

public record GetUnmappedEnvelopesQuery : IQuery<List<Domain.Envelopes.Envelope>>;
