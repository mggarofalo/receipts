using FluentAssertions;

namespace Infrastructure.IntegrationTests.CatalogRecall;

public sealed class CatalogRecallMetricsTests
{
	private static CatalogRecallOutcome Hit(int rank, params string[] channels) => new(rank, channels, channels, null, [], "Store");

	private static CatalogRecallOutcome Miss(string[] pool, int? rawSemanticRank = null) => new(null, [], pool, rawSemanticRank, [], "Store");

	[Fact]
	public void Compute_ReportsRecallAtEachCutoffAndMeanReciprocalRank()
	{
		CatalogRecallMetrics metrics = CatalogRecallMetrics.Compute(
			[Hit(1, "exact"), Hit(4, "lexical"), Hit(10, "semantic"), Miss([])]);

		metrics.Sources.Should().Be(4);
		metrics.RecallAt1.Should().Be(0.25);
		metrics.RecallAt5.Should().Be(0.5);
		metrics.RecallAt20.Should().Be(0.75);
		metrics.MeanReciprocalRank.Should().BeApproximately((1 + 0.25 + 0.1) / 4, 1e-12);
		metrics.Misses.Should().Be(1);
	}

	[Fact]
	public void Compute_AttributesHitsToEveryCarryingChannelAndToSoleChannels()
	{
		CatalogRecallMetrics metrics = CatalogRecallMetrics.Compute(
			[Hit(1, "lexical", "semantic"), Hit(2, "semantic"), Hit(3, "exact")]);

		metrics.HitsCarryingChannel["semantic"].Should().BeApproximately(2.0 / 3, 1e-12);
		metrics.HitsOnlyChannel["semantic"].Should().BeApproximately(1.0 / 3, 1e-12);
		metrics.HitsCarryingChannel["lexical"].Should().BeApproximately(1.0 / 3, 1e-12);
		metrics.HitsOnlyChannel["lexical"].Should().Be(0);
		metrics.HitsCarryingChannel["approved_example"].Should().Be(0);
	}

	[Fact]
	public void Compute_SharesMissesByTheChannelThatFoundTheTargetBeforeTheCut()
	{
		CatalogRecallMetrics metrics = CatalogRecallMetrics.Compute(
			[Miss(["lexical"]), Miss(["lexical", "semantic"], rawSemanticRank: 12), Miss([], rawSemanticRank: 3), Miss([])]);

		metrics.MissesRecoverableByChannel["lexical"].Should().Be(0.5);
		metrics.MissesRecoverableByChannel["semantic"].Should().Be(0.25);
		metrics.MissesRecoverableByChannel[CatalogRecallMetrics.UnfilteredSemantic].Should().Be(0.5);
		metrics.MissesUnreached.Should().Be(0.25, "only the last miss was found by no channel at all");
	}

	[Fact]
	public void Compute_ReturnsZerosForAnEmptyGroup()
	{
		CatalogRecallMetrics metrics = CatalogRecallMetrics.Compute([]);

		metrics.Sources.Should().Be(0);
		metrics.RecallAt20.Should().Be(0);
		metrics.MeanReciprocalRank.Should().Be(0);
		metrics.MissesUnreached.Should().Be(0);
	}

	[Fact]
	public void Percentiles_InterpolateLinearlyBetweenRanks()
	{
		Percentiles values = Percentiles.Of([0.1, 0.2, 0.3, 0.4, 0.5]);

		values.Count.Should().Be(5);
		values.P10!.Value.Should().BeApproximately(0.14, 1e-12);
		values.P50!.Value.Should().BeApproximately(0.3, 1e-12);
		values.P90!.Value.Should().BeApproximately(0.46, 1e-12);
		Percentiles.Of([]).P50.Should().BeNull();
	}

	[Fact]
	public void SemanticDistribution_ComparesTargetsWithTheNearestWrongNameAtEachFloor()
	{
		SemanticSimilarityDistribution distribution = SemanticSimilarityDistribution.Compute(
		[
			new(0.92, 0.80, ChooserMiss: true),
			new(0.78, 0.83, ChooserMiss: true),
			new(0.88, 0.70, ChooserMiss: false),
			new(null, 0.75, ChooserMiss: true),
		]);

		distribution.Sources.Should().Be(4);
		distribution.WithoutTargetVector.Should().Be(1);
		distribution.TargetAboveTopWrong.Should().BeApproximately(2.0 / 3, 1e-12);
		distribution.Margin.Count.Should().Be(3);
		SemanticFloorRow floor = distribution.Floors.Single(row => row.Floor == 0.80);
		floor.TargetsAtOrAbove.Should().BeApproximately(2.0 / 3, 1e-12);
		floor.TopWrongAtOrAbove.Should().Be(0.5);
		floor.ChooserMissesRecovered.Should().Be(0.5, "of the two misses with a target vector, only 0.92 clears 0.80");
	}
}
