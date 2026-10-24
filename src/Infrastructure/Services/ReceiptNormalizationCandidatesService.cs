using Application.Interfaces.Services;
using Application.Normalization;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class ReceiptNormalizationCandidatesService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	TimeProvider timeProvider,
	IEmbeddingService? embeddingService = null) : IReceiptNormalizationCandidates
{
	public async Task<IReadOnlyList<ReceiptNormalizationSuggestion>> GetCandidatesAsync(string actor,
		NormalizationSourceKey source, CancellationToken cancellationToken)
	{
		source = NormalizationCatalogPolicy.Normalize(source);
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		DateTimeOffset now = timeProvider.GetUtcNow();
		if (!await context.Users.AnyAsync(user => user.Id == actor && !(user.LockoutEnabled && user.LockoutEnd > now), cancellationToken))
		{ throw new NormalizationCatalogException(403, "receipt_entry_actor_required", "An enabled signed-in user is required to request receipt product suggestions."); }
		IReadOnlyList<NormalizationCatalogCandidate> candidates = await NormalizationCandidateRetrieval.RetrieveAsync(
			context, source, embeddingService, cancellationToken);
		return candidates.Select(candidate => new ReceiptNormalizationSuggestion(candidate.Id, candidate.Revision,
			candidate.CanonicalName, candidate.DisplayLabel, candidate.Channels)).ToArray();
	}
}
