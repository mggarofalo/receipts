using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Searchable catalog names, and how many have a vector in the current embedding space.</summary>
internal static class CatalogEmbeddingCoverage
{
	internal static async Task<(int Ready, int Total)> CountAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		IQueryable<NormalizedDescriptionEntity> canonical = context.NormalizedDescriptions
			.AsNoTracking()
			.Where(entity =>
				entity.Status != NormalizedDescriptionStatus.Rejected
				&& entity.CanonicalName != string.Empty);
		int total = await canonical.CountAsync(cancellationToken);
		int ready = await canonical.CountAsync(
			entity => entity.Embedding != null && entity.EmbeddingModelVersion == fingerprint,
			cancellationToken);
		return (ready, total);
	}
}
