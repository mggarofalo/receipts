using Application.Interfaces;

namespace Application.Queries.Core.Occasion;

public record GetAllOccasionsQuery(bool? IsActive = null) : IQuery<List<Domain.Envelopes.Occasion>>;
