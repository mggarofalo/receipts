using Application.Interfaces;

namespace Application.Queries.Core.Occasion;

public record GetOccasionByIdQuery(Guid Id) : IQuery<Domain.Envelopes.Occasion?>;
