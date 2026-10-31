namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>Shortlist quality for one group of cases. Shares are fractions of the stated denominator.</summary>
/// <param name="HitsCarryingChannel">Of the hits, the share whose target carried each channel.</param>
/// <param name="HitsOnlyChannel">Of the hits, the share whose target carried that channel and no other.</param>
/// <param name="MissesRecoverableByChannel">
/// Of the misses, the share each channel alone would recover: that channel found the target, but final
/// ordering and the 20-row cut dropped it. <see cref="UnfilteredSemantic"/> counts targets among the 20
/// nearest neighbours before the caller's evidence filter (the receipt chooser drops semantic-only rows).
/// </param>
/// <param name="MissesUnreached">Of the misses, the share no channel found at all.</param>
public sealed record CatalogRecallMetrics(int Sources, double RecallAt1, double RecallAt5, double RecallAt20,
	double MeanReciprocalRank, IReadOnlyDictionary<string, double> HitsCarryingChannel,
	IReadOnlyDictionary<string, double> HitsOnlyChannel, int Misses,
	IReadOnlyDictionary<string, double> MissesRecoverableByChannel, double MissesUnreached)
{
	/// <summary>Retrieval channels. RECEIPTS-1102 adds approved_neighbour here.</summary>
	public static readonly string[] Channels = ["exact", "approved_example", "lexical", "semantic"];
	public const string UnfilteredSemantic = "semantic_unfiltered";

	public static CatalogRecallMetrics Compute(IReadOnlyCollection<CatalogRecallOutcome> outcomes)
	{
		CatalogRecallOutcome[] hits = outcomes.Where(outcome => outcome.Rank is not null).ToArray();
		CatalogRecallOutcome[] misses = outcomes.Where(outcome => outcome.Rank is null).ToArray();
		double RecallAt(int k) => Share(hits.Count(hit => hit.Rank <= k), outcomes.Count);
		Dictionary<string, double> recoverable = Channels.ToDictionary(channel => channel,
			channel => Share(misses.Count(miss => miss.PoolChannels.Contains(channel)), misses.Length));
		recoverable[UnfilteredSemantic] = Share(misses.Count(miss => miss.RawSemanticRank is not null), misses.Length);
		return new(outcomes.Count, RecallAt(1), RecallAt(5), RecallAt(20),
			outcomes.Count == 0 ? 0 : hits.Sum(hit => 1.0 / hit.Rank!.Value) / outcomes.Count,
			Channels.ToDictionary(channel => channel, channel => Share(hits.Count(hit => hit.Channels.Contains(channel)), hits.Length)),
			Channels.ToDictionary(channel => channel, channel => Share(hits.Count(hit => hit.Channels.SequenceEqual([channel])), hits.Length)),
			misses.Length, recoverable,
			Share(misses.Count(miss => miss.PoolChannels.Count == 0 && miss.RawSemanticRank is null), misses.Length));
	}

	private static double Share(int count, int total) => total == 0 ? 0 : (double)count / total;
}

/// <summary>Cosine similarity of the approved target and of the nearest wrong catalog name, for one source.</summary>
/// <param name="Target">Null when the target has no vector at the current fingerprint.</param>
/// <param name="ChooserMiss">The receipt chooser's shortlist did not contain the target.</param>
public sealed record SemanticSimilarityObservation(double? Target, double? TopWrong, bool ChooserMiss);

/// <summary>Linear-interpolation percentiles (the R-7 / spreadsheet definition).</summary>
public sealed record Percentiles(int Count, double? P10, double? P25, double? P50, double? P75, double? P90)
{
	public static Percentiles Of(IEnumerable<double> values)
	{
		double[] sorted = values.Order().ToArray();
		double? At(double p)
		{
			if (sorted.Length == 0) { return null; }
			double position = p * (sorted.Length - 1);
			int lower = (int)Math.Floor(position);
			int upper = Math.Min(lower + 1, sorted.Length - 1);
			return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
		}
		return new(sorted.Length, At(0.10), At(0.25), At(0.50), At(0.75), At(0.90));
	}
}

/// <param name="TargetsAtOrAbove">Share of targets with a vector whose similarity is at or above the floor.</param>
/// <param name="TopWrongAtOrAbove">Share of sources whose nearest wrong name is at or above the floor (would be admitted).</param>
/// <param name="ChooserMissesRecovered">Share of chooser misses (target has a vector) the floor would admit.</param>
public sealed record SemanticFloorRow(double Floor, double TargetsAtOrAbove, double TopWrongAtOrAbove, double ChooserMissesRecovered);

/// <summary>Evidence for RECEIPTS-1095's similarity floor.</summary>
public sealed record SemanticSimilarityDistribution(int Sources, int WithoutTargetVector, Percentiles Target,
	Percentiles TopWrong, Percentiles Margin, double TargetAboveTopWrong, IReadOnlyList<SemanticFloorRow> Floors)
{
	public static readonly double[] CandidateFloors = [0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95];

	public static SemanticSimilarityDistribution Compute(IReadOnlyCollection<SemanticSimilarityObservation> observations)
	{
		SemanticSimilarityObservation[] withTarget = observations.Where(item => item.Target is not null).ToArray();
		SemanticSimilarityObservation[] paired = withTarget.Where(item => item.TopWrong is not null).ToArray();
		SemanticSimilarityObservation[] misses = withTarget.Where(item => item.ChooserMiss).ToArray();
		SemanticSimilarityObservation[] withWrong = observations.Where(item => item.TopWrong is not null).ToArray();
		static double Share(int count, int total) => total == 0 ? 0 : (double)count / total;
		return new(observations.Count, observations.Count - withTarget.Length,
			Percentiles.Of(withTarget.Select(item => item.Target!.Value)),
			Percentiles.Of(withWrong.Select(item => item.TopWrong!.Value)),
			Percentiles.Of(paired.Select(item => item.Target!.Value - item.TopWrong!.Value)),
			Share(paired.Count(item => item.Target > item.TopWrong), paired.Length),
			CandidateFloors.Select(floor => new SemanticFloorRow(floor,
				Share(withTarget.Count(item => item.Target >= floor), withTarget.Length),
				Share(withWrong.Count(item => item.TopWrong >= floor), withWrong.Length),
				Share(misses.Count(item => item.Target >= floor), misses.Length))).ToArray());
	}
}
