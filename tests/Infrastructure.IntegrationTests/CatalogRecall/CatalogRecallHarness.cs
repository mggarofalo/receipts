using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>Embedding state at measurement time, so a half-rebuilt corpus is not mistaken for a model regression.</summary>
public sealed record CatalogRecallCorpus(string Fingerprint, int ActiveTargets, int EmbeddedAtFingerprint)
{
	public const double WarningCoveragePercent = 95;
	public double CoveragePercent => ActiveTargets == 0 ? 0 : 100.0 * EmbeddedAtFingerprint / ActiveTargets;
	public bool CoverageBelowWarning => CoveragePercent < WarningCoveragePercent;
}

public sealed record CatalogRecallCaseSet(CatalogRecallGroundTruth GroundTruth, IReadOnlyList<CatalogRecallCase> Cases,
	int SkippedTargetNotActive, int SkippedAmbiguousTarget, int SkippedInvalidSource);

public sealed record CatalogRecallMeasurement(CatalogRecallCase Case, CatalogRecallOutcome Catalog,
	CatalogRecallOutcome ReceiptChooser, SemanticSimilarityObservation Similarity);

/// <summary>
/// Runs production retrieval for every case in both modes with the case's own evidence hidden
/// (leave-one-out through <see cref="NormalizationRetrievalProbe"/>). Read-only: nothing is revoked or written.
/// </summary>
internal sealed class CatalogRecallHarness(Func<ApplicationDbContext> createContext, RecordingEmbeddingService embedding)
{
	private const int Parallelism = 4;

	public async Task<CatalogRecallCorpus> DescribeCorpusAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = createContext();
		var active = context.NormalizedDescriptions.AsNoTracking().Where(target => target.Status == NormalizedDescriptionStatus.Active);
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		return new(fingerprint, await active.CountAsync(cancellationToken),
			await CatalogEmbeddingCoverage.Ready(context, active).CountAsync(cancellationToken));
	}

	public async Task<CatalogRecallCaseSet> LoadAsync(CatalogRecallGroundTruth groundTruth, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = createContext();
		var targets = await context.NormalizedDescriptions.AsNoTracking()
			.Select(target => new { target.Id, target.Status, target.CanonicalName, target.DisplayLabel })
			.ToDictionaryAsync(target => target.Id, cancellationToken);
		List<(NormalizationSourceKey Source, Guid TargetId, Guid? MappingId)> rows = groundTruth switch
		{
			CatalogRecallGroundTruth.ApprovedMapping => (await context.NormalizationSourceMappings.AsNoTracking()
				.Where(mapping => mapping.State == "active" && !mapping.Exclude && mapping.TargetId != null)
				.Select(mapping => new { mapping.Id, mapping.Location, mapping.Description, mapping.ItemCode, TargetId = mapping.TargetId!.Value })
				.ToListAsync(cancellationToken))
				.Select(row => (new NormalizationSourceKey(row.Location, row.Description, row.ItemCode), row.TargetId, (Guid?)row.Id)).ToList(),
			_ => (await (from item in context.ReceiptItems.AsNoTracking()
						 join receipt in context.Receipts.AsNoTracking() on item.ReceiptId equals receipt.Id
						 where item.NormalizationOrigin == "legacy_unknown" && item.NormalizedDescriptionId != null
							 && item.DeletedAt == null && receipt.DeletedAt == null
						 select new { receipt.Location, item.Description, item.ReceiptItemCode, TargetId = item.NormalizedDescriptionId!.Value })
				.ToListAsync(cancellationToken))
				.Select(row => (new NormalizationSourceKey(row.Location, row.Description, row.ReceiptItemCode), row.TargetId, (Guid?)null)).ToList(),
		};
		int invalid = 0, notActive = 0, ambiguous = 0;
		Dictionary<NormalizationSourceKey, List<(Guid TargetId, Guid? MappingId)>> links = [];
		foreach (var row in rows)
		{
			NormalizationSourceKey? source = TryNormalize(row.Source);
			if (source is null) { invalid++; continue; }
			if (!links.TryGetValue(source, out var sourceLinks)) { links[source] = sourceLinks = []; }
			sourceLinks.Add((row.TargetId, row.MappingId));
		}
		List<CatalogRecallCase> cases = [];
		foreach (var (source, sourceLinks) in links)
		{
			Guid[] distinctTargets = sourceLinks.Select(link => link.TargetId).Distinct().ToArray();
			if (distinctTargets.Length > 1) { ambiguous++; continue; }
			if (!targets.TryGetValue(distinctTargets[0], out var target) || target.Status != NormalizedDescriptionStatus.Active)
			{ notActive++; continue; }
			cases.Add(new(source, target.Id, target.CanonicalName, target.DisplayLabel, sourceLinks[0].MappingId));
		}
		return new(groundTruth, cases.OrderBy(item => item.Source.Location, StringComparer.Ordinal)
			.ThenBy(item => item.Source.Description, StringComparer.Ordinal).ThenBy(item => item.Source.ItemCode, StringComparer.Ordinal).ToArray(),
			notActive, ambiguous, invalid);
	}

	public async Task<IReadOnlyList<CatalogRecallMeasurement>> RunAsync(CatalogRecallCaseSet set, CancellationToken cancellationToken)
	{
		(string CanonicalName, string? DisplayLabel)[] catalog;
		await using (ApplicationDbContext context = createContext())
		{
			catalog = (await context.NormalizedDescriptions.AsNoTracking().Where(target => target.Status == NormalizedDescriptionStatus.Active)
				.Select(target => new { target.CanonicalName, target.DisplayLabel }).ToListAsync(cancellationToken))
				.Select(target => (target.CanonicalName, target.DisplayLabel)).ToArray();
		}
		HashSet<string> shared = set.Cases.GroupBy(item => item.Source.Description, StringComparer.Ordinal)
			.Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
		CatalogRecallMeasurement[] measurements = new CatalogRecallMeasurement[set.Cases.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, set.Cases.Count),
			new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
			async (index, token) =>
			{
				CatalogRecallCase item = set.Cases[index];
				IReadOnlyList<string> strata = CatalogRecallStrata.Classify(item, shared.Contains(item.Source.Description),
					CatalogRecallStrata.HasCrowdedToken(item.Source.Description, catalog));
				measurements[index] = await MeasureAsync(set.GroundTruth, item, strata, token);
			});
		return measurements;
	}

	private async Task<CatalogRecallMeasurement> MeasureAsync(CatalogRecallGroundTruth groundTruth, CatalogRecallCase item,
		IReadOnlyList<string> strata, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = createContext();
		NormalizationRetrievalProbe catalogProbe = Probe(groundTruth, item);
		var catalog = await NormalizationCandidateRetrieval.RetrieveAsync(context, item.Source, embedding, cancellationToken, probe: catalogProbe);
		NormalizationRetrievalProbe chooserProbe = Probe(groundTruth, item);
		var chooser = await NormalizationCandidateRetrieval.RetrieveForReceiptEntryAsync(context, item.Source, embedding, cancellationToken, chooserProbe);
		(int? rawRank, double? target, double? topWrong) = await SemanticAsync(context, item, cancellationToken);
		CatalogRecallOutcome Outcome(IReadOnlyList<NormalizationCatalogCandidate> shortlist, NormalizationRetrievalProbe probe)
		{
			int index = shortlist.ToList().FindIndex(candidate => candidate.Id == item.TargetId);
			return new(index < 0 ? null : index + 1, index < 0 ? [] : shortlist[index].Channels,
				probe.Pool.GetValueOrDefault(item.TargetId) ?? [], rawRank, strata, item.Source.Location);
		}
		CatalogRecallOutcome chooserOutcome = Outcome(chooser, chooserProbe);
		return new(item, Outcome(catalog, catalogProbe), chooserOutcome,
			new(target, topWrong, chooserOutcome.Rank is null));
	}

	/// <summary>
	/// Leave-one-out. An approved case hides its own mapping from the approved_example channel, where other
	/// stores' mappings for the same description stay visible as in production. Its description is hidden
	/// from every candidate's displayed examples, for every store, so the shortlist never shows the answer.
	/// A legacy link is not evidence any channel reads, so nothing needs hiding for it.
	/// </summary>
	private static NormalizationRetrievalProbe Probe(CatalogRecallGroundTruth groundTruth, CatalogRecallCase item)
		=> groundTruth == CatalogRecallGroundTruth.ApprovedMapping
			? new() { HiddenMappingId = item.MappingId, HiddenDescription = item.Source.Description }
			: new();

	/// <summary>The production semantic query, unfiltered, with similarities the shortlist does not expose.</summary>
	private async Task<(int? RawRank, double? Target, double? TopWrong)> SemanticAsync(ApplicationDbContext context,
		CatalogRecallCase item, CancellationToken cancellationToken)
	{
		float[]? recorded = embedding.Recorded(item.Source.Description);
		if (recorded is not { Length: > 0 }) { return (null, null, null); }
		Vector query = new(recorded);
		// The same kNN rule and similarity production uses, so partial coverage does not under-measure recall.
		List<CatalogNeighbour> nearest = await NearestNeighbourSearch.ToListAsync(context.Database,
			CatalogSemanticNeighbours.Nearest(context.Database, query, 21), NullLogger.Instance, cancellationToken);
		List<double> target = await CatalogSemanticNeighbours.SimilarityOf(context.Database, query, item.TargetId)
			.ToListAsync(cancellationToken);
		int index = nearest.Take(20).ToList().FindIndex(neighbour => neighbour.Id == item.TargetId);
		return (index < 0 ? null : index + 1, target.Count == 0 ? null : target[0],
			nearest.FirstOrDefault(neighbour => neighbour.Id != item.TargetId)?.Similarity);
	}

	private static NormalizationSourceKey? TryNormalize(NormalizationSourceKey source)
	{
		try { return NormalizationCatalogPolicy.Normalize(source); }
		catch (NormalizationCatalogException) { return null; }
	}
}
