using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.GetEmbeddingCoverage;

public sealed record GetEmbeddingCoverageQuery : IRequest<EmbeddingCoverage>;
