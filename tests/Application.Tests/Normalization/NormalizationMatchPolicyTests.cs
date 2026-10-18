using Application.Models.NormalizedDescriptions;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;

namespace Application.Tests.Normalization;

public class NormalizationMatchPolicyTests
{
	[Theory]
	[InlineData(0.9, MatchDecision.ReuseSimilar)]
	[InlineData(0.899999, MatchDecision.CreatePendingReview)]
	[InlineData(0.5, MatchDecision.CreatePendingReview)]
	[InlineData(0.499999, MatchDecision.CreateNew)]
	[InlineData(null, MatchDecision.CreateNew)]
	public void Decide_ThresholdBoundaries_AreInclusive(double? similarity, MatchDecision expected)
	{
		NormalizationMatchPolicy policy = new(0.9, 0.5);
		policy.Decide(null, true, similarity).Should().Be(expected);
	}

	[Theory]
	[InlineData(NormalizedDescriptionStatus.Active, MatchDecision.ReuseExact)]
	[InlineData(NormalizedDescriptionStatus.PendingReview, MatchDecision.ReuseExact)]
	[InlineData(NormalizedDescriptionStatus.Rejected, MatchDecision.Rejected)]
	public void Decide_ExactMatch_PrecedesEmbeddingAvailability(NormalizedDescriptionStatus status, MatchDecision expected)
	{
		NormalizationMatchPolicy policy = new(0.9, 0.5);
		policy.Decide(status, false, null).Should().Be(expected);
	}

	[Fact]
	public void Decide_UnavailableEmbedding_HasExplicitFallback()
	{
		NormalizationMatchPolicy policy = new(0.9, 0.5);
		policy.Decide(null, false, null).Should().Be(MatchDecision.EmbeddingUnavailable);
	}

	[Fact]
	public void Preview_UsesSamePopulationAndKeepsUnscoredItemsUnresolved()
	{
		ThresholdImpactPreview actual = ThresholdImpactCalculator.Calculate(
			[null, 0.95, 0.9, 0.8, 0.5, 0.4], new(0.9, 0.5), new(0.8, 0.4));

		actual.Current.Should().Be(new ClassificationCounts(2, 2, 2));
		actual.Proposed.Should().Be(new ClassificationCounts(3, 2, 1));
		actual.Deltas.Should().Be(new ReclassificationDeltas(0, 1, 0, 1));
	}

	[Fact]
	public void Preview_RaisedThreshold_ReportsAutoToPending()
	{
		ThresholdImpactPreview actual = ThresholdImpactCalculator.Calculate(
			[0.9, 0.8, null], new(0.8, 0.4), new(0.95, 0.8));
		actual.Current.Should().Be(new ClassificationCounts(2, 0, 1));
		actual.Proposed.Should().Be(new ClassificationCounts(0, 2, 1));
		actual.Deltas.Should().Be(new ReclassificationDeltas(2, 0, 0, 0));
	}
}
