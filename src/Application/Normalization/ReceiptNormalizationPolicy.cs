using Domain.NormalizedDescriptions;

namespace Application.Normalization;

public sealed record ReceiptNormalizationDecision(NormalizationReviewState State, string Reason);

/// <summary>Receipt verification is reviewed evidence, independent of registry or assistance lifecycle.</summary>
public sealed record ReceiptVerificationFacts(bool SourceCurrent, bool AssignmentCurrent, bool IdentityCurrent,
	bool TargetActive, bool IndependentlyReviewed, bool MappingCurrent, NormalizationReviewState State);

public static class ReceiptNormalizationPolicy
{
	public static bool EvidenceIsCurrent(ReceiptVerificationFacts facts)
		=> facts.SourceCurrent && facts.AssignmentCurrent && facts.IdentityCurrent
			&& facts.State is NormalizationReviewState.Verified or NormalizationReviewState.Excluded
			&& (facts.State == NormalizationReviewState.Excluded || facts.TargetActive)
			&& (facts.IndependentlyReviewed || facts.MappingCurrent);

	public static void Validate(ReceiptNormalizationIntent intent, string sourceFingerprint)
	{
		if (!Enum.IsDefined(intent.Kind) || intent.ClientRowId == Guid.Empty
			|| string.IsNullOrWhiteSpace(intent.SourceFingerprint)
			|| intent.TargetId == Guid.Empty || intent.ExpectedTargetRevision == Guid.Empty
			|| intent.ExpectedMappingRevision == Guid.Empty || intent.ExpectedItemRevision == Guid.Empty)
		{
			throw Error(400, "invalid_normalization_intent", "Supply a valid normalization choice and stable draft row ID.");
		}
		if (!string.Equals(intent.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal))
		{
			throw Error(409, "normalization_source_changed", "The store, description or item code changed. Refresh the choice or explicitly defer review.");
		}
		bool existing = intent.Kind == ReceiptNormalizationIntentKind.ConfirmExisting;
		bool create = intent.Kind == ReceiptNormalizationIntentKind.ConfirmNew;
		if ((intent.Kind == ReceiptNormalizationIntentKind.KnownSource && intent.ExpectedMappingRevision is null)
			|| existing != (intent.TargetId is not null && intent.ExpectedTargetRevision is not null)
			|| (!existing && (intent.TargetId is not null || intent.ExpectedTargetRevision is not null))
			|| create != !string.IsNullOrWhiteSpace(intent.NewName)
			|| intent.NewName?.Trim().Length > 200
			|| (intent.Kind == ReceiptNormalizationIntentKind.Exclude && string.IsNullOrWhiteSpace(intent.Reason))
			|| intent.Reason?.Length > 500
			|| (intent.RememberSource && intent.Kind is not (ReceiptNormalizationIntentKind.ConfirmExisting or ReceiptNormalizationIntentKind.ConfirmNew or ReceiptNormalizationIntentKind.Exclude)))
		{
			throw Error(400, "invalid_normalization_intent", "The selected normalization action has missing or incompatible fields.");
		}
	}

	public static ReceiptNormalizationDecision Decide(ReceiptNormalizationIntentKind? intent, bool approvedMapping, bool mappingExcluded)
		=> intent switch
		{
			ReceiptNormalizationIntentKind.ConfirmExisting or ReceiptNormalizationIntentKind.ConfirmNew => new(NormalizationReviewState.Verified, "purchase_review"),
			ReceiptNormalizationIntentKind.Exclude => new(NormalizationReviewState.Excluded, "purchase_exclusion"),
			ReceiptNormalizationIntentKind.Defer => new(NormalizationReviewState.NeedsReview, "deferred"),
			_ when approvedMapping => new(mappingExcluded ? NormalizationReviewState.Excluded : NormalizationReviewState.Verified, "approved_mapping"),
			_ => new(NormalizationReviewState.NeedsReview, "unknown_source")
		};

	private static NormalizationCatalogException Error(int status, string code, string detail) => new(status, code, detail);
}
