using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Infrastructure.IntegrationTests.CatalogRecall;

public sealed record CatalogRecallModeReport(CatalogRecallMetrics Overall, IReadOnlyDictionary<string, CatalogRecallMetrics> Strata,
	IReadOnlyDictionary<string, CatalogRecallMetrics> Stores, SemanticSimilarityDistribution? SemanticSimilarity,
	SemanticSimilarityDistribution? SemanticSimilarityWithoutExactNames);

/// <summary>
/// One measurement. <see cref="Aggregate"/> groups stores into anonymised rank buckets and is the only
/// form that may be committed or logged; <see cref="Private"/> names stores and stays local.
/// </summary>
public sealed record CatalogRecallReport(string GroundTruth, string GroundTruthNote, DateTimeOffset MeasuredAt,
	CatalogRecallCorpus Corpus, double CoveragePercent, int Sources, int SkippedTargetNotActive, int SkippedAmbiguousTarget,
	int SkippedInvalidSource, CatalogRecallModeReport Catalog, CatalogRecallModeReport ReceiptChooser)
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	public static CatalogRecallReport Aggregate(CatalogRecallCaseSet set, CatalogRecallCorpus corpus,
		IReadOnlyList<CatalogRecallMeasurement> measurements, DateTimeOffset measuredAt)
	{
		Dictionary<string, int> storeRanks = measurements.GroupBy(item => item.Case.Source.Location, StringComparer.Ordinal)
			.OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
			.Select((group, index) => (group.Key, Rank: index + 1)).ToDictionary(item => item.Key, item => item.Rank, StringComparer.Ordinal);
		return Build(set, corpus, measurements, measuredAt, store => storeRanks[store] switch
		{
			1 => "store_rank_1",
			<= 5 => "store_rank_2_5",
			_ => "store_rank_6_plus",
		});
	}

	public static CatalogRecallReport Private(CatalogRecallCaseSet set, CatalogRecallCorpus corpus,
		IReadOnlyList<CatalogRecallMeasurement> measurements, DateTimeOffset measuredAt)
		=> Build(set, corpus, measurements, measuredAt, store => store);

	private static CatalogRecallReport Build(CatalogRecallCaseSet set, CatalogRecallCorpus corpus,
		IReadOnlyList<CatalogRecallMeasurement> measurements, DateTimeOffset measuredAt, Func<string, string> storeLabel)
	{
		CatalogRecallModeReport Mode(Func<CatalogRecallMeasurement, CatalogRecallOutcome> outcome, bool semantic) => new(
			CatalogRecallMetrics.Compute(measurements.Select(outcome).ToArray()),
			CatalogRecallStrata.All.ToDictionary(stratum => stratum, stratum => CatalogRecallMetrics.Compute(
				measurements.Select(outcome).Where(item => item.Strata.Contains(stratum)).ToArray())),
			measurements.Select(outcome).GroupBy(item => storeLabel(item.Store), StringComparer.Ordinal)
				.OrderBy(group => group.Key, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => CatalogRecallMetrics.Compute(group.ToArray())),
			semantic ? SemanticSimilarityDistribution.Compute(measurements.Select(item => item.Similarity).ToArray()) : null,
			// Exact names sit at similarity ~1 and say nothing about a floor for text that differs from the name.
			semantic ? SemanticSimilarityDistribution.Compute(measurements
				.Where(item => !item.ReceiptChooser.Strata.Contains(CatalogRecallStrata.ExactName))
				.Select(item => item.Similarity).ToArray()) : null);
		return new(set.GroundTruth == CatalogRecallGroundTruth.ApprovedMapping ? "approved_mapping" : "legacy_link",
			set.GroundTruth == CatalogRecallGroundTruth.ApprovedMapping
				? "Human-approved: active, non-excluded source mappings, leave-one-out."
				: "PROXY, NOT HUMAN-APPROVED: purchases the legacy resolver linked (origin legacy_unknown). Many canonical names were created from receipt text, so exact and semantic are flattered; compare exact_name with not_exact_name.",
			measuredAt, corpus, corpus.CoveragePercent, set.Cases.Count, set.SkippedTargetNotActive, set.SkippedAmbiguousTarget,
			set.SkippedInvalidSource, Mode(item => item.Catalog, semantic: false), Mode(item => item.ReceiptChooser, semantic: true));
	}

	public string ToJson() => JsonSerializer.Serialize(this, Json);

	public string ToMarkdown()
	{
		StringBuilder text = new();
		text.AppendLine(CultureInfo.InvariantCulture, $"### Ground truth: `{GroundTruth}`");
		text.AppendLine().AppendLine(GroundTruthNote).AppendLine();
		text.AppendLine(CultureInfo.InvariantCulture, $"- Measured: {MeasuredAt:yyyy-MM-dd HH:mm} UTC");
		text.AppendLine(CultureInfo.InvariantCulture, $"- Embedding fingerprint: `{Corpus.Fingerprint}`");
		text.AppendLine(CultureInfo.InvariantCulture,
			$"- Coverage: {Corpus.EmbeddedAtFingerprint}/{Corpus.ActiveTargets} active catalog names ({CoveragePercent:0.0}%){(Corpus.CoverageBelowWarning ? " — BELOW 95%, semantic numbers are not comparable" : "")}");
		text.AppendLine(CultureInfo.InvariantCulture,
			$"- Sources evaluated: {Sources} (skipped: {SkippedTargetNotActive} target not active, {SkippedAmbiguousTarget} ambiguous target, {SkippedInvalidSource} invalid key)");
		foreach ((string name, CatalogRecallModeReport mode) in new[] { ("Catalog source-run", Catalog), ("Receipt chooser", ReceiptChooser) })
		{
			text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"#### {name}").AppendLine();
			text.AppendLine("| Group | Sources | R@1 | R@5 | R@20 | MRR |").AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
			foreach ((string group, CatalogRecallMetrics metrics) in mode.Strata.Prepend(new("all", mode.Overall)).Concat(mode.Stores))
			{
				text.AppendLine(CultureInfo.InvariantCulture,
					$"| {group} | {metrics.Sources} | {Percent(metrics.RecallAt1)} | {Percent(metrics.RecallAt5)} | {Percent(metrics.RecallAt20)} | {metrics.MeanReciprocalRank:0.000} |");
			}
			text.AppendLine().AppendLine("| Channel | Hits carrying | Hits only this | Misses it alone recovers |").AppendLine("| --- | ---: | ---: | ---: |");
			foreach (string channel in CatalogRecallMetrics.Channels.Append(CatalogRecallMetrics.UnfilteredSemantic))
			{
				text.AppendLine(CultureInfo.InvariantCulture,
					$"| {channel} | {Optional(mode.Overall.HitsCarryingChannel, channel)} | {Optional(mode.Overall.HitsOnlyChannel, channel)} | {Percent(mode.Overall.MissesRecoverableByChannel[channel])} |");
			}
			text.AppendLine().AppendLine(CultureInfo.InvariantCulture,
				$"Misses: {mode.Overall.Misses}; no channel found the target for {Percent(mode.Overall.MissesUnreached)} of them.");
			if (mode.SemanticSimilarity is { } similarity) { AppendSimilarity(text, "all sources", similarity); }
			if (mode.SemanticSimilarityWithoutExactNames is { } inexact) { AppendSimilarity(text, CatalogRecallStrata.NotExactName, inexact); }
		}
		return text.ToString();
	}

	private static void AppendSimilarity(StringBuilder text, string group, SemanticSimilarityDistribution similarity)
	{
		text.AppendLine().AppendLine(CultureInfo.InvariantCulture,
			$"Semantic similarity (cosine), expected target vs nearest wrong catalog name — {group}:").AppendLine();
		text.AppendLine("| Series | n | p10 | p25 | p50 | p75 | p90 |").AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
		foreach ((string name, Percentiles values) in new[] { ("target", similarity.Target), ("top wrong", similarity.TopWrong), ("target − top wrong", similarity.Margin) })
		{
			text.AppendLine(CultureInfo.InvariantCulture,
				$"| {name} | {values.Count} | {Number(values.P10)} | {Number(values.P25)} | {Number(values.P50)} | {Number(values.P75)} | {Number(values.P90)} |");
		}
		text.AppendLine().AppendLine(CultureInfo.InvariantCulture,
			$"Target beats the nearest wrong name for {Percent(similarity.TargetAboveTopWrong)}; {similarity.WithoutTargetVector} targets had no current vector.");
		text.AppendLine().AppendLine("| Floor | Targets ≥ floor | Top wrong ≥ floor | Chooser misses admitted |").AppendLine("| ---: | ---: | ---: | ---: |");
		foreach (SemanticFloorRow row in similarity.Floors)
		{
			text.AppendLine(CultureInfo.InvariantCulture,
				$"| {row.Floor:0.00} | {Percent(row.TargetsAtOrAbove)} | {Percent(row.TopWrongAtOrAbove)} | {Percent(row.ChooserMissesRecovered)} |");
		}
	}

	private static string Optional(IReadOnlyDictionary<string, double> values, string key)
		=> values.TryGetValue(key, out double value) ? Percent(value) : "—";

	private static string Percent(double value) => value.ToString("0.0%", CultureInfo.InvariantCulture);

	private static string Number(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "—";
}
