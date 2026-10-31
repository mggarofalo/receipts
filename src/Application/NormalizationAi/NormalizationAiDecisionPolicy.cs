using Domain.NormalizedDescriptions;

namespace Application.NormalizationAi;

/// <summary>Observed business state; storage adapters supply these facts while holding write guards.</summary>
public sealed record NormalizationAiSourceState(Guid Revision, string Description, Guid? CanonicalId,
	Guid? CanonicalRevision, NormalizedDescriptionStatus? CanonicalStatus,
	bool ItemActive = true, bool ParentActive = true, bool TemplateProtected = false, bool ExactRejected = false);

/// <summary>
/// Owns review-first normalization decisions. Infrastructure reads facts and commits an entire validated
/// selection atomically; neither provider output nor database mechanics may relax these rules.
/// </summary>
public static class NormalizationAiDecisionPolicy
{
	public static readonly string[] ReservingStates = ["queued", "running", "ready", "unknown"];

	public static void RequireAdmission(int pendingRuns, int configuredLimit)
	{
		if (pendingRuns >= Math.Clamp(configuredLimit, 1, 100))
		{
			throw new NormalizationAiException(409, "pending_run_limit",
				"Finish or discard existing runs before starting another normalization request.");
		}
	}

	public static DateTimeOffset UnacceptedRetentionCutoff(DateTimeOffset now, int configuredDays)
		=> now.AddDays(-Math.Clamp(configuredDays, 1, 365));

	public static bool IsEligibleSource(NormalizationAiSourceState source)
		=> source.ItemActive && source.ParentActive && !source.TemplateProtected && !source.ExactRejected
			&& !string.IsNullOrWhiteSpace(source.Description)
			&& (source.CanonicalId is null || source.CanonicalStatus == NormalizedDescriptionStatus.PendingReview);

	public static void RequireRetryable(string state)
	{
		if (state is not ("failed" or "unknown"))
		{ throw new NormalizationAiException(409, "run_conflict", "Only a failed or unknown run can be retried explicitly."); }
	}

	public static void RequireCancellable(string state)
	{
		if (state == "applied")
		{ throw new NormalizationAiException(409, "run_conflict", "An applied run cannot be cancelled."); }
	}
}
