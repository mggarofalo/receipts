using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Searchable catalog names, and how many have a vector in the current embedding space.</summary>
internal static class CatalogEmbeddingCoverage
{
	internal static async Task<(int Ready, int Total)> CountAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		IQueryable<NormalizedDescriptionEntity> canonical = context.NormalizedDescriptions
			.AsNoTracking()
			.Where(entity =>
				entity.Status != NormalizedDescriptionStatus.Rejected
				&& entity.CanonicalName != string.Empty);
		int total = await canonical.CountAsync(cancellationToken);
		int ready = await Ready(context, canonical).CountAsync(cancellationToken);
		return (ready, total);
	}

	/// <summary>
	/// Names whose vectors are all current: the canonical-name vector, and the label vector when the name
	/// has a display label (RECEIPTS-1096). A rename therefore leaves the name not ready until it is rebuilt.
	/// </summary>
	internal static IQueryable<NormalizedDescriptionEntity> Ready(ApplicationDbContext context, IQueryable<NormalizedDescriptionEntity> names)
	{
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		return names.Where(entity => entity.Embedding != null && entity.EmbeddingModelVersion == fingerprint
			&& (entity.DisplayLabel == null || context.ItemEmbeddings.Any(label =>
				label.EntityType == CatalogSemanticNeighbours.LabelEntityType
				&& label.EntityId == entity.Id
				&& label.EntityText == entity.DisplayLabel
				&& label.ModelVersion == fingerprint)));
	}
}
