namespace Domain.NormalizedDescriptions;

public enum ReceiptNormalizationIntentKind
{
	KnownSource,
	ConfirmExisting,
	ConfirmNew,
	Exclude,
	Defer
}

public enum NormalizationReviewState
{
	NeedsReview,
	Verified,
	Excluded
}

/// <summary>A user's choice for one source observation; never a client assertion of verification.</summary>
public sealed record ReceiptNormalizationIntent(
	ReceiptNormalizationIntentKind Kind,
	Guid ClientRowId,
	string SourceFingerprint,
	Guid? TargetId = null,
	Guid? ExpectedTargetRevision = null,
	Guid? ExpectedMappingRevision = null,
	Guid? ExpectedItemRevision = null,
	string? NewName = null,
	bool RememberSource = false,
	string? Reason = null);
