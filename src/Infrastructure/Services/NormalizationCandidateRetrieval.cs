using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Infrastructure.Services;

/// <summary>Shared bounded retrieval. Callers must apply their own authorization before invoking it.</summary>
internal static class NormalizationCandidateRetrieval
{
	private const int MaximumReceiptScanPerChannel = 500;

	internal static async Task<IReadOnlyList<NormalizationCatalogCandidate>> RetrieveAsync(
		ApplicationDbContext context, NormalizationSourceKey source, IEmbeddingService? embeddingService,
				CancellationToken cancellationToken, Func<NormalizationCatalogCandidate, bool>? include = null,
				Func<string, IEnumerable<string>>? lexicalTerms = null)
	{
		Dictionary<Guid, (NormalizedDescriptionEntity Target, HashSet<string> Channels)> found = [];
		Dictionary<Guid, int> semanticRanks = [];
		async Task AddAsync(IQueryable<NormalizedDescriptionEntity> query, string channel)
		{
			var orderedQuery = query.OrderBy(target => target.CanonicalName).ThenBy(target => target.Id);
			// Suggestions are best effort; full catalog search remains available.
			// Keep both database work and per-keystroke transfer bounded.
			var candidates = orderedQuery.Take(include is null ? 20 : MaximumReceiptScanPerChannel);
			int accepted = 0;
			await foreach (var target in candidates.AsAsyncEnumerable().WithCancellation(cancellationToken))
			{
				if (!found.TryGetValue(target.Id, out var existing)) { existing = (target, []); }
				if (include is not null)
				{
					string[] channels = existing.Channels.Append(channel).Distinct().ToArray();
					if (!include(new(target.Id, target.NormalizationRevision, target.CanonicalName,
							target.DisplayLabel, channels, []))) { continue; }
				}
				if (!found.ContainsKey(target.Id)) { found.Add(target.Id, existing); }
				existing.Channels.Add(channel);
				if (++accepted == 20) { break; }
			}
		}
		var active = context.NormalizedDescriptions.AsNoTracking().Where(target => target.Status == NormalizedDescriptionStatus.Active);
		string text = source.Description.ToLowerInvariant();
		await AddAsync(active.Where(target => target.CanonicalName.ToLower() == text || (target.DisplayLabel ?? target.CanonicalName).ToLower() == text), "exact");
		// Approved examples are evidence, never self-training on automatic links.
		await AddAsync(active.Where(target => context.NormalizationSourceMappings.Any(mapping => mapping.State == "active" && !mapping.Exclude
			&& mapping.TargetId == target.Id && mapping.Description == source.Description)), "approved_example");
		IEnumerable<string> terms = lexicalTerms?.Invoke(source.Description)
				?? source.Description.Split([' ', '\t', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
						.Where(token => token.Length >= 2);
		string[] tokens = terms.Select(token => token.ToLowerInvariant()).Distinct().Take(8).ToArray();
		foreach (string token in tokens)
		{
			await AddAsync(active.Where(target => target.CanonicalName.ToLower().Contains(token) || (target.DisplayLabel ?? target.CanonicalName).ToLower().Contains(token)), "lexical");
		}
		if (context.Database.IsNpgsql() && embeddingService?.IsConfigured == true)
		{
			float[]? vector = await embeddingService.GenerateEmbeddingAsync(source.Description, cancellationToken);
			if (vector is { Length: > 0 })
			{
				Vector embedding = new(vector);
				var nearest = await context.Database.SqlQuery<Guid>($"""
					SELECT "Id" AS "Value" FROM matching."NormalizedDescriptions"
					WHERE "Status" = 'Active' AND "Embedding" IS NOT NULL
					AND "EmbeddingModelVersion" = {OnnxEmbeddingService.EmbeddingSpaceFingerprint}
					ORDER BY "Embedding" <=> {embedding}::vector, "Id" LIMIT 20
					""").ToListAsync(cancellationToken);
				for (int rank = 0; rank < nearest.Count; rank++) { semanticRanks[nearest[rank]] = rank; }
				await AddAsync(active.Where(target => nearest.Contains(target.Id)), "semantic");
			}
		}
		List<NormalizationCatalogCandidate> result = [];
		// Reserve space for the best semantic neighbors: alphabetically early lexical
		// distractors must not evict the closest product before identity verification.
		// Apply the caller's display-evidence requirement before the
		// limit so weak candidates cannot crowd out useful ones.
		var eligible = found.Values.Where(entry => include is null || include(new(
				entry.Target.Id, entry.Target.NormalizationRevision, entry.Target.CanonicalName,
				entry.Target.DisplayLabel, entry.Channels.ToArray(), []))).ToArray();
		var ordered = eligible.Where(entry => entry.Channels.Contains("exact")).Take(2)
				.Concat(eligible.Where(entry => entry.Channels.Contains("approved_example")).OrderBy(entry => entry.Target.CanonicalName, StringComparer.Ordinal).Take(4))
				.Concat(eligible.Where(entry => semanticRanks.ContainsKey(entry.Target.Id)).OrderBy(entry => semanticRanks[entry.Target.Id]).Take(8))
				.Concat(eligible.OrderByDescending(entry => entry.Channels.Count)
		.ThenBy(entry => semanticRanks.GetValueOrDefault(entry.Target.Id, int.MaxValue)).ThenBy(entry => entry.Target.CanonicalName, StringComparer.Ordinal))
	.DistinctBy(entry => entry.Target.Id).Take(20).ToArray();
		Guid[] candidateIds = ordered.Select(entry => entry.Target.Id).ToArray();
		var exampleRows = await active.Where(target => candidateIds.Contains(target.Id)).Select(target => new
		{
			target.Id,
			Examples = context.NormalizationSourceMappings.Where(mapping => mapping.State == "active" && !mapping.Exclude && mapping.TargetId == target.Id)
				.Select(mapping => mapping.Description).Distinct().Order().Take(3).ToList()
		}).ToListAsync(cancellationToken);
		var examplesByTarget = exampleRows.ToDictionary(row => row.Id, row => row.Examples);
		foreach (var entry in ordered)
		{
			result.Add(new(entry.Target.Id, entry.Target.NormalizationRevision, entry.Target.CanonicalName, entry.Target.DisplayLabel,
				entry.Channels.Order().ToArray(), examplesByTarget.GetValueOrDefault(entry.Target.Id) ?? []));
		}
		return result;
	}
}
