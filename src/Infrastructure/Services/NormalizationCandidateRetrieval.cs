using System.Text.RegularExpressions;
using Application.Interfaces.Services;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;

namespace Infrastructure.Services;

/// <summary>
/// Evaluation-only view into one retrieval, for the RECEIPTS-1089 recall harness; production callers never pass it.
/// Leave-one-out hides a source's own evidence here instead of revoking rows, which would invalidate reviews.
/// </summary>
internal sealed class NormalizationRetrievalProbe
{
	/// <summary>This mapping cannot supply <c>approved_example</c> evidence.</summary>
	public Guid? HiddenMappingId { get; init; }

	/// <summary>This description never appears among any candidate's approved examples.</summary>
	public string? HiddenDescription { get; init; }

	/// <summary>Every target a channel accepted, with its channels, before final ordering and the 20-row cut.</summary>
	public IReadOnlyDictionary<Guid, string[]> Pool { get; internal set; } = new Dictionary<Guid, string[]>();
}

/// <summary>Shared bounded retrieval. Callers must apply their own authorization before invoking it.</summary>
internal static class NormalizationCandidateRetrieval
{
	/// <summary>Rows the one relevance-ranked lexical query returns; receipt entry filters them in that order.</summary>
	private const int LexicalLimit = 200;

	/// <summary>Trigrams need three characters: a shorter LIKE term cannot use the index and would make the whole filter scan.</summary>
	private const int MinimumIndexedTokenLength = 3;

	/// <summary>The receipt-entry chooser: only candidates with useful display evidence survive.</summary>
	internal static Task<IReadOnlyList<NormalizationCatalogCandidate>> RetrieveForReceiptEntryAsync(
		ApplicationDbContext context, NormalizationSourceKey source, IEmbeddingService? embeddingService,
		CancellationToken cancellationToken, NormalizationRetrievalProbe? probe = null, ILogger? logger = null)
		=> RetrieveAsync(context, source, embeddingService, cancellationToken,
			candidate => ReceiptSuggestionEvidence.IsUseful(source, candidate), ReceiptSuggestionEvidence.LexicalTerms, probe, logger);

	/// <summary>
	/// Suggestions are best effort: a full inference queue or an unavailable model degrades to the
	/// non-semantic channels instead of failing the request. Caller cancellation still propagates.
	/// </summary>
	internal static async Task<float[]?> TryEmbedAsync(IEmbeddingService embeddingService, string text, ILogger logger,
		CancellationToken cancellationToken)
	{
		try
		{
			return await embeddingService.GenerateEmbeddingAsync(text, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Semantic candidate retrieval unavailable; using exact, approved and lexical evidence only");
			return null;
		}
	}

	/// <summary>The lowercase terms the lexical channel probes, one bounded query each.</summary>
	internal static string[] LexicalTokens(string description, Func<string, IEnumerable<string>>? lexicalTerms = null)
	{
		IEnumerable<string> terms = lexicalTerms?.Invoke(description)
				?? description.Split([' ', '\t', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
						.Where(token => token.Length >= 2);
		return terms.Select(token => token.ToLowerInvariant()).Distinct().Take(8).ToArray();
	}

	internal static async Task<IReadOnlyList<NormalizationCatalogCandidate>> RetrieveAsync(
		ApplicationDbContext context, NormalizationSourceKey source, IEmbeddingService? embeddingService,
				CancellationToken cancellationToken, Func<NormalizationCatalogCandidate, bool>? include = null,
				Func<string, IEnumerable<string>>? lexicalTerms = null, NormalizationRetrievalProbe? probe = null,
				ILogger? logger = null)
	{
		Guid? hiddenMappingId = probe?.HiddenMappingId;
		string? hiddenDescription = probe?.HiddenDescription;
		Dictionary<Guid, (NormalizedDescriptionEntity Target, HashSet<string> Channels)> found = [];
		Dictionary<Guid, int> semanticRanks = [];
		Dictionary<Guid, double> semanticSimilarities = [];
		Dictionary<Guid, int> lexicalScores = [];
		Dictionary<Guid, int> lexicalRanks = [];
		NormalizationCatalogCandidate Candidate(NormalizedDescriptionEntity target, string[] channels, IReadOnlyList<string> examples)
			=> new(target.Id, target.NormalizationRevision, target.CanonicalName, target.DisplayLabel, channels, examples,
				semanticSimilarities.TryGetValue(target.Id, out double similarity) ? similarity : null);
		// Suggestions are best effort; full catalog search remains available.
		// Keep both database work and per-keystroke transfer bounded.
		Task<List<NormalizedDescriptionEntity>> AlphabeticalAsync(IQueryable<NormalizedDescriptionEntity> query)
			=> query.OrderBy(target => target.CanonicalName).ThenBy(target => target.Id).Take(20).ToListAsync(cancellationToken);
		// Accepts at most 20 rows per channel, in the order the channel ranked them.
		void Add(IReadOnlyList<NormalizedDescriptionEntity> ranked, string channel)
		{
			int accepted = 0;
			foreach (var target in ranked)
			{
				if (!found.TryGetValue(target.Id, out var existing)) { existing = (target, []); }
				if (include is not null)
				{
					string[] channels = existing.Channels.Append(channel).Distinct().ToArray();
					// Only the semantic channel may rest on similarity; otherwise a near neighbour would be
					// recorded, labelled and capped as if it carried exact, approved or lexical evidence.
					NormalizationCatalogCandidate candidate = Candidate(target, channels, []);
					if (!include(channel == "semantic" ? candidate : candidate with { SemanticSimilarity = null })) { continue; }
				}
				if (!found.ContainsKey(target.Id)) { found.Add(target.Id, existing); }
				existing.Channels.Add(channel);
				if (++accepted == 20) { break; }
			}
		}
		var active = context.NormalizedDescriptions.AsNoTracking().Where(target => target.Status == NormalizedDescriptionStatus.Active);
		// Neighbours are found first so every channel's evidence filter can see their similarity;
		// the semantic channel still scans last, after the specific evidence channels.
		List<Guid> nearest = [];
		if (context.Database.IsNpgsql() && embeddingService?.IsConfigured == true)
		{
			float[]? vector = await TryEmbedAsync(embeddingService, source.Description, logger ?? NullLogger.Instance, cancellationToken);
			if (vector is { Length: > 0 })
			{
				Vector embedding = new(vector);
				List<SemanticNeighbour> neighbours = await NearestNeighbourSearch.ToListAsync(context.Database, context.Database.SqlQuery<SemanticNeighbour>($"""
					SELECT "Id", 1 - ("Embedding" <=> {embedding}::vector) AS "Similarity" FROM matching."NormalizedDescriptions"
					WHERE "Status" = 'Active' AND "Embedding" IS NOT NULL
					AND "EmbeddingModelVersion" = {OnnxEmbeddingService.EmbeddingSpaceFingerprint}
					ORDER BY "Embedding" <=> {embedding}::vector, "Id" LIMIT 20
					"""), logger ?? NullLogger.Instance, cancellationToken);
				for (int rank = 0; rank < neighbours.Count; rank++)
				{
					semanticRanks[neighbours[rank].Id] = rank;
					semanticSimilarities[neighbours[rank].Id] = neighbours[rank].Similarity;
				}
				nearest = neighbours.Select(neighbour => neighbour.Id).ToList();
			}
		}
		string text = source.Description.ToLowerInvariant();
		Add(await AlphabeticalAsync(active.Where(target => target.CanonicalName.ToLower() == text || (target.DisplayLabel ?? target.CanonicalName).ToLower() == text)), "exact");
		// Approved examples are evidence, never self-training on automatic links.
		Add(await AlphabeticalAsync(active.Where(target => context.NormalizationSourceMappings.Any(mapping => mapping.State == "active" && !mapping.Exclude
			&& mapping.TargetId == target.Id && mapping.Description == source.Description
			&& (hiddenMappingId == null || mapping.Id != hiddenMappingId)))), "approved_example");
		string[] tokens = LexicalTokens(source.Description, lexicalTerms);
		// The relevance query needs pg_trgm, like the semantic channel needs pgvector.
		if (tokens.Length > 0 && context.Database.IsNpgsql())
		{
			List<LexicalHit> hits = await LexicalAsync(context, tokens, source.Description, cancellationToken);
			Guid[] hitIds = hits.Select(hit => hit.Id).ToArray();
			Dictionary<Guid, NormalizedDescriptionEntity> rows = await active.Where(target => hitIds.Contains(target.Id))
				.ToDictionaryAsync(target => target.Id, cancellationToken);
			for (int rank = 0; rank < hits.Count; rank++)
			{
				lexicalScores[hits[rank].Id] = hits[rank].Score;
				lexicalRanks[hits[rank].Id] = rank;
			}
			Add(hits.Where(hit => rows.ContainsKey(hit.Id)).Select(hit => rows[hit.Id]).ToList(), "lexical");
		}
		if (nearest.Count > 0) { Add(await AlphabeticalAsync(active.Where(target => nearest.Contains(target.Id))), "semantic"); }
		if (probe is not null) { probe.Pool = found.ToDictionary(entry => entry.Key, entry => entry.Value.Channels.Order().ToArray()); }
		List<NormalizationCatalogCandidate> result = [];
		// Reserve space for the best semantic neighbors: alphabetically early lexical
		// distractors must not evict the closest product before identity verification.
		// Apply the caller's display-evidence requirement before the
		// limit so weak candidates cannot crowd out useful ones.
		var eligible = found.Values.Where(entry => include is null
				|| include(Candidate(entry.Target, entry.Channels.ToArray(), []))).ToArray();
		IEnumerable<(NormalizedDescriptionEntity Target, HashSet<string> Channels)> Rank(
			IReadOnlyCollection<(NormalizedDescriptionEntity Target, HashSet<string> Channels)> entries)
			=> entries.Where(entry => entry.Channels.Contains("exact")).Take(2)
				.Concat(entries.Where(entry => entry.Channels.Contains("approved_example")).OrderBy(entry => entry.Target.CanonicalName, StringComparer.Ordinal).Take(4))
				.Concat(entries.Where(entry => semanticRanks.ContainsKey(entry.Target.Id)).OrderBy(entry => semanticRanks[entry.Target.Id]).Take(8))
				// Words matched, not channels carried: a name matching every source word outranks one
				// matching a single word, however many channels found the latter.
				.Concat(entries.OrderByDescending(entry => lexicalScores.GetValueOrDefault(entry.Target.Id))
					.ThenBy(entry => semanticRanks.GetValueOrDefault(entry.Target.Id, int.MaxValue))
					.ThenBy(entry => lexicalRanks.GetValueOrDefault(entry.Target.Id, int.MaxValue))
					.ThenBy(entry => entry.Target.CanonicalName, StringComparer.Ordinal));
		// Receipt entry (RECEIPTS-1095): a row the caller admits only for its semantic similarity ranks
		// after every row with exact, approved or useful lexical evidence, so a near neighbour never
		// displaces a whole-word match, including in the inline top five.
		HashSet<Guid> bySimilarityAlone = include is null ? [] : eligible
			.Where(entry => !include(Candidate(entry.Target, entry.Channels.ToArray(), []) with { SemanticSimilarity = null }))
			.Select(entry => entry.Target.Id).ToHashSet();
		var ordered = Rank(eligible.Where(entry => !bySimilarityAlone.Contains(entry.Target.Id)).ToArray())
			.Concat(eligible.Where(entry => bySimilarityAlone.Contains(entry.Target.Id)).OrderBy(entry => semanticRanks[entry.Target.Id]))
			.DistinctBy(entry => entry.Target.Id).Take(20).ToArray();
		Guid[] candidateIds = ordered.Select(entry => entry.Target.Id).ToArray();
		var exampleRows = await active.Where(target => candidateIds.Contains(target.Id)).Select(target => new
		{
			target.Id,
			Examples = context.NormalizationSourceMappings.Where(mapping => mapping.State == "active" && !mapping.Exclude && mapping.TargetId == target.Id
					&& (hiddenDescription == null || mapping.Description != hiddenDescription))
				.Select(mapping => mapping.Description).Distinct().Order().Take(3).ToList()
		}).ToListAsync(cancellationToken);
		var examplesByTarget = exampleRows.ToDictionary(row => row.Id, row => row.Examples);
		foreach (var entry in ordered)
		{
			result.Add(Candidate(entry.Target, entry.Channels.Order().ToArray(), examplesByTarget.GetValueOrDefault(entry.Target.Id) ?? []));
		}
		return result;
	}

	/// <summary>
	/// The lexical channel in one query (RECEIPTS-1094). A name scores the number of distinct source tokens
	/// it contains as whole words; a name that only contains a token inside a longer word scores zero
	/// and ranks after every whole-word match. Ties break on trigram similarity to the description.
	/// The filter is only LIKE terms on the two trigram-indexed expressions, so a bitmap index scan
	/// answers it; the regex scoring runs on the filtered rows alone. Tokens are lowered in .NET and
	/// names by PostgreSQL lower(), which agree for non-ASCII letters under the UTF-8 locale the
	/// deployed database uses.
	/// </summary>
	private static Task<List<LexicalHit>> LexicalAsync(ApplicationDbContext context, string[] tokens, string description,
		CancellationToken cancellationToken)
	{
		(string sql, NpgsqlParameter[] parameters) = LexicalQuery(tokens, description);
		return context.Database.SqlQueryRaw<LexicalHit>(sql, parameters).ToListAsync(cancellationToken);
	}

	internal static (string Sql, NpgsqlParameter[] Parameters) LexicalQuery(string[] tokens, string description)
	{
		List<NpgsqlParameter> parameters = [];
		string Parameter(object value)
		{
			parameters.Add(new NpgsqlParameter($"p{parameters.Count}", value));
			return $"@p{parameters.Count - 1}";
		}
		// A whole word is bounded by the start or end of the name or by a character that is neither a
		// letter nor a digit, so tokens such as "(lg)" match too. Other characters are escaped; surrogate
		// halves are left alone so a supplementary character is not split.
		string words = Parameter(tokens.Select(token =>
			$"(^|[^[:alnum:]]){Regex.Replace(token, @"[^\p{L}\p{N}\p{Cs}]", @"\$0")}($|[^[:alnum:]])").ToArray());
		string text = Parameter(description.ToLowerInvariant());
		// Short tokens still score but only filter when nothing longer exists.
		string[] filtered = tokens.Where(token => token.Length >= MinimumIndexedTokenLength).ToArray();
		string filter = string.Join(" OR ", (filtered.Length > 0 ? filtered : tokens).Select(token =>
		{
			string pattern = Parameter($"%{Regex.Replace(token, @"[\\%_]", @"\$0")}%");
			return $"""lower("CanonicalName") LIKE {pattern} OR lower(COALESCE("DisplayLabel", "CanonicalName")) LIKE {pattern}""";
		}));
		string sql = $"""
			SELECT "Id",
				(SELECT count(*)::int FROM unnest({words}::text[]) AS word
					WHERE lower("CanonicalName") ~ word OR lower(COALESCE("DisplayLabel", "CanonicalName")) ~ word) AS "Score",
				similarity(lower(COALESCE("DisplayLabel", "CanonicalName")), {text})::double precision AS "Similarity"
			FROM matching."NormalizedDescriptions"
			WHERE "Status" = 'Active' AND ({filter})
			ORDER BY "Score" DESC, "Similarity" DESC, "CanonicalName", "Id"
			LIMIT {LexicalLimit}
			""";
		return (sql, parameters.ToArray());
	}

	private sealed class LexicalHit
	{
		public Guid Id { get; set; }
		public int Score { get; set; }
		public double Similarity { get; set; }
	}

	private sealed class SemanticNeighbour
	{
		public Guid Id { get; set; }
		public double Similarity { get; set; }
	}
}
