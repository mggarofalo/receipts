namespace Application.Models.NormalizedDescriptions;

// Exact deletion preview for pending catalog cleanup. Unlinked purchases require review.
// Fingerprint prevents deleting a changed set.
public record RequeuePendingPreview(
	int PendingDescriptionCount,
	string PendingFingerprint,
	int LinkedItemCount);
