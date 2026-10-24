using Application.Normalization;

namespace Infrastructure.Services;

public sealed partial class NormalizationCatalogService
{
	public async Task<IReadOnlyList<NormalizationCatalogCandidate>> GetCandidatesAsync(string actor, NormalizationSourceKey source, CancellationToken cancellationToken)
	{
		source = NormalizationCatalogPolicy.Normalize(source);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await AuthorizeAsync(context, actor, cancellationToken);
		return await NormalizationCandidateRetrieval.RetrieveAsync(context, source, embeddingService, cancellationToken);
	}
}
