namespace Application.Models.NormalizedDescriptions;

// Exact deletion preview for pending catalog cleanup. Unlinked purchases require review.
// Fingerprint prevents deleting a changed set. Legacy estimate fields remain zero for wire
// compatibility: automatic similarity rematching is no longer scheduled.
public record RequeuePendingPreview(
	int PendingDescriptionCount,
	string PendingFingerprint,
	int LinkedItemCount,
	int EstimatedResolverCycles,
	int EstimatedCatchUpSeconds);
