using System.Globalization;
using System.Text;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Fixtures;
using Xunit.Abstractions;

namespace Infrastructure.Tests.Services;

/// <summary>
/// Calibration test for embedding similarity thresholds used by the normalized
/// description registry feature. Generates embeddings for known-equivalent clusters
/// of grocery descriptions and records the cosine-similarity distribution both
/// within clusters (should be high) and across clusters (should be low). The output
/// is written to calibration-results.md at the repo root for offline review.
///
/// This test asserts nothing about the distribution itself — its value is in the
/// numeric report. Future maintainers can re-run it after model changes by invoking
/// <c>dotnet test --filter "FullyQualifiedName~OnnxEmbeddingThresholdCalibrationTests"</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Model")]
public class OnnxEmbeddingThresholdCalibrationTests : IClassFixture<OnnxEmbeddingServiceFixture>
{
	private readonly OnnxEmbeddingService _service;
	private readonly ITestOutputHelper _output;

	public OnnxEmbeddingThresholdCalibrationTests(OnnxEmbeddingServiceFixture fixture, ITestOutputHelper output)
	{
		_service = fixture.Service;
		_output = output;
	}

	[Fact]
	public async Task CalibrateThresholds_ProducesDistributionReport()
	{
		Dictionary<string, List<string>> clusters = new()
		{
			["Grapes"] =
			[
				"Red Grapes",
				"Green Grapes",
				"Red Seedless Grapes 2LB",
				"Green Seedless Grapes",
				"Organic Red Grapes",
				"Grapes Red 1LB",
			],
			["Canned Tomatoes"] =
			[
				"Diced Tomatoes 14.5oz",
				"Diced Tomatoes 28oz",
				"Crushed Tomatoes 28oz",
				"Canned Diced Tomatoes",
				"Whole Peeled Tomatoes 28oz",
			],
			["Bread"] =
			[
				"Whole Wheat Bread",
				"White Bread Loaf",
				"Sourdough Bread",
				"Wheat Bread",
				"Rye Bread",
			],
			["Milk"] =
			[
				"Whole Milk 1 Gallon",
				"2% Milk 1/2 Gallon",
				"Skim Milk Gallon",
				"Organic Whole Milk",
				"Lactose-Free Milk",
			],
			["Apples"] =
			[
				"Honeycrisp Apples",
				"Granny Smith Apples",
				"Red Delicious Apples 3lb",
				"Fuji Apples",
				"Pink Lady Apples",
			],
		};

		// Generate embeddings for every description, keyed by cluster.
		Dictionary<string, List<(string Text, float[] Embedding)>> embeddingsByCluster = new();
		foreach ((string clusterName, List<string> descriptions) in clusters)
		{
			List<float[]> embeddings = await _service.GenerateEmbeddingsAsync(descriptions, CancellationToken.None);
			embeddingsByCluster[clusterName] = descriptions
				.Zip(embeddings, (text, embedding) => (text, embedding))
				.ToList();
		}

		// Sanity check: all embeddings are normalized to the correct dimension.
		embeddingsByCluster.Values
			.SelectMany(list => list)
			.Should()
			.AllSatisfy(entry =>
			{
				entry.Embedding.Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension);
				float norm = MathF.Sqrt(entry.Embedding.Sum(x => x * x));
				norm.Should().BeApproximately(1.0f, 1e-5f);
			});

		// Within-cluster pairs.
		List<PairResult> withinPairs = new();
		foreach ((string clusterName, List<(string Text, float[] Embedding)> entries) in embeddingsByCluster)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				for (int j = i + 1; j < entries.Count; j++)
				{
					double sim = CosineSimilarity(entries[i].Embedding, entries[j].Embedding);
					withinPairs.Add(new PairResult(clusterName, clusterName, entries[i].Text, entries[j].Text, sim));
				}
			}
		}

		// Cross-cluster pairs.
		List<PairResult> crossPairs = new();
		List<KeyValuePair<string, List<(string Text, float[] Embedding)>>> clusterList = embeddingsByCluster.ToList();
		for (int ci = 0; ci < clusterList.Count; ci++)
		{
			for (int cj = ci + 1; cj < clusterList.Count; cj++)
			{
				foreach ((string Text, float[] Embedding) a in clusterList[ci].Value)
				{
					foreach ((string Text, float[] Embedding) b in clusterList[cj].Value)
					{
						double sim = CosineSimilarity(a.Embedding, b.Embedding);
						crossPairs.Add(new PairResult(clusterList[ci].Key, clusterList[cj].Key, a.Text, b.Text, sim));
					}
				}
			}
		}

		// Build the report.
		StringBuilder report = new();
		report.AppendLine("# OnnxEmbeddingService Threshold Calibration Results");
		report.AppendLine();
		report.AppendLine($"Model: `{OnnxEmbeddingService.ModelName}` ({OnnxEmbeddingService.EmbeddingDimension} dim, L2-normalized, {OnnxEmbeddingService.PoolingStrategyName}-pooled)");
		report.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
		report.AppendLine();

		report.AppendLine("## Per-Cluster Within-Cluster Similarity");
		report.AppendLine();
		report.AppendLine("| Cluster | N pairs | Min | Max | Mean | Median |");
		report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
		foreach (string clusterName in clusters.Keys)
		{
			List<double> sims = withinPairs
				.Where(p => p.ClusterA == clusterName)
				.Select(p => p.Similarity)
				.OrderBy(x => x)
				.ToList();
			if (sims.Count == 0)
			{
				continue;
			}

			double median = sims.Count % 2 == 0
				? (sims[sims.Count / 2 - 1] + sims[sims.Count / 2]) / 2.0
				: sims[sims.Count / 2];
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"| {clusterName} | {sims.Count} | {sims.Min():F4} | {sims.Max():F4} | {sims.Average():F4} | {median:F4} |"));
		}

		report.AppendLine();
		report.AppendLine("## Overall Within-Cluster Similarity");
		report.AppendLine();
		List<double> allWithin = withinPairs.Select(p => p.Similarity).OrderBy(x => x).ToList();
		double withinMedian = allWithin.Count % 2 == 0
			? (allWithin[allWithin.Count / 2 - 1] + allWithin[allWithin.Count / 2]) / 2.0
			: allWithin[allWithin.Count / 2];
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Pairs: **{allWithin.Count}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Min: **{allWithin.Min():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Max: **{allWithin.Max():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Mean: **{allWithin.Average():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Median: **{withinMedian:F4}**"));

		report.AppendLine();
		report.AppendLine("## Overall Cross-Cluster Similarity");
		report.AppendLine();
		List<double> allCross = crossPairs.Select(p => p.Similarity).OrderBy(x => x).ToList();
		double crossMedian = allCross.Count % 2 == 0
			? (allCross[allCross.Count / 2 - 1] + allCross[allCross.Count / 2]) / 2.0
			: allCross[allCross.Count / 2];
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Pairs: **{allCross.Count}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Min: **{allCross.Min():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Max: **{allCross.Max():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Mean: **{allCross.Average():F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Median: **{crossMedian:F4}**"));

		report.AppendLine();
		report.AppendLine("## Top 10 Highest Within-Cluster Similarities");
		report.AppendLine();
		report.AppendLine("| Cluster | Text A | Text B | Similarity |");
		report.AppendLine("| --- | --- | --- | ---: |");
		foreach (PairResult pair in withinPairs.OrderByDescending(p => p.Similarity).Take(10))
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"| {pair.ClusterA} | {pair.TextA} | {pair.TextB} | {pair.Similarity:F4} |"));
		}

		report.AppendLine();
		report.AppendLine("## Top 10 Highest Cross-Cluster Similarities");
		report.AppendLine();
		report.AppendLine("(These should be LOW. If any are high, thresholds need careful tuning.)");
		report.AppendLine();
		report.AppendLine("| Cluster A | Cluster B | Text A | Text B | Similarity |");
		report.AppendLine("| --- | --- | --- | --- | ---: |");
		foreach (PairResult pair in crossPairs.OrderByDescending(p => p.Similarity).Take(10))
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"| {pair.ClusterA} | {pair.ClusterB} | {pair.TextA} | {pair.TextB} | {pair.Similarity:F4} |"));
		}

		report.AppendLine();
		report.AppendLine("## Band Overlap Diagnostic");
		report.AppendLine();
		double withinMin = allWithin.Min();
		double crossMax = allCross.Max();
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Within-cluster MIN: **{withinMin:F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Cross-cluster MAX: **{crossMax:F4}**"));
		if (withinMin > crossMax)
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- Gap: **{withinMin - crossMax:F4}** (clean separation)"));
		}
		else
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- Overlap: **{crossMax - withinMin:F4}** (bands overlap — see recommendation)"));
		}

		report.AppendLine();
		report.AppendLine("## Threshold Recommendations");
		report.AppendLine();
		report.AppendLine("These values are derived from the measured distribution. Re-running this test");
		report.AppendLine("after changing the model or the description corpus will regenerate them.");
		report.AppendLine();

		// Within-cluster p10 — catches the bulk of true equivalents while excluding the tail
		// caused by weak clusters (e.g., apple cultivars). Cosine p10 is the 10th percentile.
		double withinP10 = Percentile(allWithin, 0.10);
		double withinP25 = Percentile(allWithin, 0.25);
		double withinP50 = Percentile(allWithin, 0.50);
		double crossP95 = Percentile(allCross, 0.95);
		double crossP99 = Percentile(allCross, 0.99);

		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Within-cluster p10 / p25 / p50: **{withinP10:F4} / {withinP25:F4} / {withinP50:F4}**"));
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"- Cross-cluster p95 / p99 / max: **{crossP95:F4} / {crossP99:F4} / {allCross.Max():F4}**"));
		report.AppendLine();

		if (withinMin > crossMax)
		{
			// Clean separation: put auto-accept just below within-min, pending-review at the gap midpoint.
			double autoAccept = Math.Round(withinMin - 0.02, 2);
			double pendingReview = Math.Round((withinMin + crossMax) / 2, 2);
			report.AppendLine("Bands do not overlap. Recommended defaults:");
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- `AutoAcceptThreshold`: **{autoAccept:F2}** (just below within-cluster min)"));
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- `PendingReviewThreshold`: **{pendingReview:F2}** (midpoint of the gap)"));
		}
		else
		{
			// Bands overlap. Auto-accept must sit above cross-p99 to avoid false merges; pending-review
			// sits around cross-p95 so borderline cases bubble up to a human. True equivalents below
			// auto-accept are accepted as a false-negative trade: a human will confirm them in review.
			double autoAccept = Math.Round(Math.Max(crossP99, withinP50) + 0.01, 2);
			double pendingReview = Math.Round(crossP95, 2);
			report.AppendLine("**Bands overlap.** The model is not fully discriminative for this corpus");
			report.AppendLine("(e.g., cultivar-vs-cultivar apples score lower than fruit-vs-fruit cross pairs).");
			report.AppendLine("Recommended compromise defaults — prefer false-negatives (human review) over");
			report.AppendLine("false-positives (wrong merges):");
			report.AppendLine();
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- `AutoAcceptThreshold`: **{autoAccept:F2}** (above cross-p99 {crossP99:F4} and within-p50 {withinP50:F4})"));
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- `PendingReviewThreshold`: **{pendingReview:F2}** (around cross-p95 {crossP95:F4})"));
			report.AppendLine();
			int autoAcceptHits = withinPairs.Count(p => p.Similarity >= autoAccept);
			int autoAcceptCrossHits = crossPairs.Count(p => p.Similarity >= autoAccept);
			int pendingHits = withinPairs.Count(p => p.Similarity >= pendingReview && p.Similarity < autoAccept);
			int pendingCrossHits = crossPairs.Count(p => p.Similarity >= pendingReview && p.Similarity < autoAccept);
			report.AppendLine("At these thresholds:");
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- Auto-accept would fire on {autoAcceptHits}/{withinPairs.Count} true-equivalent pairs and {autoAcceptCrossHits}/{crossPairs.Count} distinct pairs"));
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"- Pending-review would catch {pendingHits} additional true-equivalent pairs and {pendingCrossHits} distinct pairs for human verification"));
		}

		report.AppendLine();
		report.AppendLine("## All Within-Cluster Pairs (sorted ascending)");
		report.AppendLine();
		report.AppendLine("| Cluster | Text A | Text B | Similarity |");
		report.AppendLine("| --- | --- | --- | ---: |");
		foreach (PairResult pair in withinPairs.OrderBy(p => p.Similarity))
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"| {pair.ClusterA} | {pair.TextA} | {pair.TextB} | {pair.Similarity:F4} |"));
		}

		report.AppendLine();
		report.AppendLine("## All Cross-Cluster Pairs (sorted descending, top 30)");
		report.AppendLine();
		report.AppendLine("| Cluster A | Cluster B | Text A | Text B | Similarity |");
		report.AppendLine("| --- | --- | --- | --- | ---: |");
		foreach (PairResult pair in crossPairs.OrderByDescending(p => p.Similarity).Take(30))
		{
			report.AppendLine(string.Create(CultureInfo.InvariantCulture,
				$"| {pair.ClusterA} | {pair.ClusterB} | {pair.TextA} | {pair.TextB} | {pair.Similarity:F4} |"));
		}

		string resolvedPath = ResolveRepoRootPath("calibration-results.md");
		await File.WriteAllTextAsync(resolvedPath, report.ToString(), Encoding.UTF8);

		_output.WriteLine($"Calibration report written to: {resolvedPath}");
		_output.WriteLine($"Within-cluster: n={allWithin.Count}, min={allWithin.Min():F4}, max={allWithin.Max():F4}, mean={allWithin.Average():F4}");
		_output.WriteLine($"Cross-cluster:  n={allCross.Count}, min={allCross.Min():F4}, max={allCross.Max():F4}, mean={allCross.Average():F4}");
	}

	/// <summary>
	/// Linear-interpolated percentile on a pre-sorted (ascending) list.
	/// </summary>
	private static double Percentile(List<double> sortedAscending, double p)
	{
		if (sortedAscending.Count == 0)
		{
			return 0;
		}

		if (sortedAscending.Count == 1)
		{
			return sortedAscending[0];
		}

		double rank = p * (sortedAscending.Count - 1);
		int lo = (int)Math.Floor(rank);
		int hi = (int)Math.Ceiling(rank);
		if (lo == hi)
		{
			return sortedAscending[lo];
		}

		double frac = rank - lo;
		return sortedAscending[lo] + frac * (sortedAscending[hi] - sortedAscending[lo]);
	}

	private static double CosineSimilarity(float[] a, float[] b)
	{
		double dot = 0;
		for (int i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
		}

		// Both vectors are L2-normalized, so dot product = cosine similarity.
		return dot;
	}

	private static string ResolveRepoRootPath(string fileName)
	{
		// Walk up from the test output directory until we find a directory containing a .git folder
		// (or a known sentinel file), then drop the file there.
		string? dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "Receipts.slnx")))
			{
				return Path.Combine(dir, fileName);
			}

			DirectoryInfo? parent = Directory.GetParent(dir);
			if (parent is null)
			{
				break;
			}

			dir = parent.FullName;
		}

		// Fallback: just drop it next to the test binary.
		return Path.Combine(AppContext.BaseDirectory, fileName);
	}

	private sealed record PairResult(string ClusterA, string ClusterB, string TextA, string TextB, double Similarity);
}
