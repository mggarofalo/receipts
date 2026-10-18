using Application.Models.NormalizedDescriptions;

namespace Application.Normalization;

/// <summary>Reclassifies recorded evidence; it does not rerun matching or infer link provenance.</summary>
public static class ThresholdImpactCalculator
{
	public static ThresholdImpactPreview Calculate(IEnumerable<double?> scores,
		NormalizationMatchPolicy currentPolicy, NormalizationMatchPolicy proposedPolicy)
	{
		int currentAuto = 0, currentPending = 0, currentUnresolved = 0;
		int proposedAuto = 0, proposedPending = 0, proposedUnresolved = 0;
		int autoToPending = 0, pendingToAuto = 0, unresolvedToAuto = 0, unresolvedToPending = 0;
		foreach (double? score in scores)
		{
			MatchScoreBand current = currentPolicy.Classify(score);
			MatchScoreBand proposed = proposedPolicy.Classify(score);
			Count(current, ref currentAuto, ref currentPending, ref currentUnresolved);
			Count(proposed, ref proposedAuto, ref proposedPending, ref proposedUnresolved);
			switch (current, proposed)
			{
				case (MatchScoreBand.AutoAccepted, MatchScoreBand.PendingReview): autoToPending++; break;
				case (MatchScoreBand.PendingReview, MatchScoreBand.AutoAccepted): pendingToAuto++; break;
				case (MatchScoreBand.Unresolved, MatchScoreBand.AutoAccepted): unresolvedToAuto++; break;
				case (MatchScoreBand.Unresolved, MatchScoreBand.PendingReview): unresolvedToPending++; break;
			}
		}
		return new(new(currentAuto, currentPending, currentUnresolved),
			new(proposedAuto, proposedPending, proposedUnresolved),
			new(autoToPending, pendingToAuto, unresolvedToAuto, unresolvedToPending));
	}

	private static void Count(MatchScoreBand band, ref int auto, ref int pending, ref int unresolved)
	{
		switch (band)
		{
			case MatchScoreBand.AutoAccepted: auto++; break;
			case MatchScoreBand.PendingReview: pending++; break;
			default: unresolved++; break;
		}
	}
}
