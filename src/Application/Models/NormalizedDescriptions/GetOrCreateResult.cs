using Domain.NormalizedDescriptions;

namespace Application.Models.NormalizedDescriptions;

// Returned by INormalizedDescriptionService.GetOrCreateAsync. The resolver (RECEIPTS-578)
// uses MatchScore to populate ReceiptItem.NormalizedDescriptionMatchScore at the same time
// it writes the NormalizedDescriptionId FK, so admins can later query threshold-impact
// aggregates without recomputing embeddings. MatchScore is null when no ANN candidate was
// above the pending-review floor (a brand-new canonical entry was created) or when the
// embedding service was unavailable.
public record GetOrCreateResult(NormalizedDescription Description, double? MatchScore)
{
	/// <summary>
	/// The returned row is a tombstone: a reviewer rejected this text, and the caller must not
	/// link receipt items to it (RECEIPTS-876).
	/// </summary>
	/// <remarks>
	/// Derived from the row's own status rather than carried as a separate flag, so the two can
	/// never disagree. Only the exact-match branch can return a Rejected row — the ANN search
	/// filters tombstones out, so they can be neither auto-accepted nor cited as a near-miss.
	/// </remarks>
	public bool IsRejected => Description.Status == NormalizedDescriptionStatus.Rejected;
}
