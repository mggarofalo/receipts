using Domain.NormalizedDescriptions;

namespace Application.Normalization;

/// <summary>Classifies advisory threshold simulations from observations, without persistence or model calls.</summary>
public sealed class NormalizationMatchPolicy
{
	public double AutoAcceptThreshold { get; }
	public double PendingReviewThreshold { get; }

	public NormalizationMatchPolicy(double autoAcceptThreshold, double pendingReviewThreshold)
	{
		NormalizedDescriptionSettings.Validate(autoAcceptThreshold, pendingReviewThreshold);
		AutoAcceptThreshold = autoAcceptThreshold;
		PendingReviewThreshold = pendingReviewThreshold;
	}

	public MatchDecision Decide(NormalizedDescriptionStatus? exactStatus, bool embeddingAvailable, double? similarity)
	{
		if (exactStatus is not null)
		{
			return exactStatus == NormalizedDescriptionStatus.Rejected ? MatchDecision.Rejected : MatchDecision.ReuseExact;
		}

		if (!embeddingAvailable)
		{
			return MatchDecision.EmbeddingUnavailable;
		}

		return Classify(similarity) switch
		{
			MatchScoreBand.AutoAccepted => MatchDecision.ReuseSimilar,
			MatchScoreBand.PendingReview => MatchDecision.CreatePendingReview,
			_ => MatchDecision.CreateNew,
		};
	}

	public MatchScoreBand Classify(double? score)
	{
		if (score >= AutoAcceptThreshold)
		{
			return MatchScoreBand.AutoAccepted;
		}
		return score >= PendingReviewThreshold ? MatchScoreBand.PendingReview : MatchScoreBand.Unresolved;
	}
}
