using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Says once, per embedding fingerprint, that the catalog is not yet embedded in the current space.
/// Until the rebuild finishes, semantic retrieval silently shrinks (docs/semantic-vector-rebuild.md).
/// </summary>
internal sealed class EmbeddingCoverageWarning(ILogger logger)
{
	internal const double MinimumCoverage = 0.95;

	private string? warnedFingerprint;

	internal async Task CheckAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		if (warnedFingerprint == fingerprint)
		{
			return;
		}

		int ready, total;
		try
		{
			(ready, total) = await CatalogEmbeddingCoverage.CountAsync(context, cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			// A diagnostic must never stop the rebuild it reports on.
			logger.LogDebug(ex, "Catalog embedding coverage check failed");
			return;
		}

		if (total == 0 || ready >= total * MinimumCoverage)
		{
			return;
		}

		warnedFingerprint = fingerprint;
		logger.LogWarning(
			"Only {Ready} of {Total} catalog names ({Coverage:P1}) have vectors at embedding fingerprint {Fingerprint}; " +
			"semantic suggestions are incomplete until the rebuild finishes",
			ready, total, (double)ready / total, fingerprint);
	}
}
