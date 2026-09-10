using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.GetEmbeddingCoverage;

public sealed class GetEmbeddingCoverageQueryHandler(INormalizedDescriptionService service)
	: IRequestHandler<GetEmbeddingCoverageQuery, EmbeddingCoverage>
{
	public ValueTask<EmbeddingCoverage> Handle(
		GetEmbeddingCoverageQuery request,
		CancellationToken cancellationToken) =>
		new(service.GetEmbeddingCoverageAsync(cancellationToken));
}
