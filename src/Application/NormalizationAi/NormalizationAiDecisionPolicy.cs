using Domain.NormalizedDescriptions;

namespace Application.NormalizationAi;

/// <summary>Observed business state; storage adapters supply these facts while holding write guards.</summary>
public sealed record NormalizationAiSourceState(Guid Revision, string Description, Guid? CanonicalId,
	Guid? CanonicalRevision, NormalizedDescriptionStatus? CanonicalStatus, double? MatchScore,
	bool ItemActive = true, bool ParentActive = true, bool TemplateProtected = false, bool ExactRejected = false);
public sealed record NormalizationAiCanonicalState(Guid Id, Guid Revision, NormalizedDescriptionStatus Status);
public sealed record NormalizationAiReviewState(string State, string Action);

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

	public static void RequireUnchangedSource(NormalizationAiSourceState expected, NormalizationAiSourceState? current)
	{
		if (current is null || !IsEligibleSource(current) || current.Revision != expected.Revision
			|| current.Description != expected.Description || current.CanonicalId != expected.CanonicalId
			|| current.CanonicalRevision != expected.CanonicalRevision || current.MatchScore != expected.MatchScore)
		{
			throw new NormalizationAiException(409, "source_changed",
				"A source item changed or is protected. Nothing was applied; cancel and create a fresh run.");
		}
	}

	public static void RequireValidTarget(string action, string? canonicalName, Guid? targetId, Guid? targetRevision,
		NormalizationAiCanonicalState? current, bool nameExists)
	{
		if (action == "reuse" && current is not null && current.Id == targetId
			&& current.Revision == targetRevision && current.Status == NormalizedDescriptionStatus.Active) { return; }
		// A reviewed new name has the same semantics as a deliberate manual split. It does not rename
		// the source canonical or activate an existing pending/rejected row, which would affect other items.
		if (action == "create" && targetId is null && !nameExists && !string.IsNullOrWhiteSpace(canonicalName)
			&& canonicalName.Length <= 200 && !canonicalName.Any(char.IsControl)) { return; }
		throw new NormalizationAiException(409, "target_changed",
			"A proposed canonical target changed or its name is already present. Nothing was applied; refresh the review.");
	}

	public static void RequireReviewable(string state, Guid revision, Guid expectedRevision, IReadOnlyList<NormalizationAiReviewState> selected)
	{
		if (state != "ready" || revision != expectedRevision || selected.Any(proposal => proposal.State != "pending"
			|| proposal.Action is not ("reuse" or "create")))
		{
			throw new NormalizationAiException(409, "run_conflict",
				"The run changed or the selection contains a proposal that cannot be applied. Refresh the review.");
		}
	}

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
