namespace Application.Models.NormalizedDescriptions;

// What a requeue actually did (RECEIPTS-883).
//
// Counts describe live receipt items only, matching the convention MergeAsync established: trashed
// rows pointing at a deleted description are unlinked too — leaving them would let a restore from
// the recycle bin resurrect an item pointing at a row that no longer exists — but they are not
// counted, so the admin-facing number lines up with what a report would show.
//
// ClearedMatchScoreCount is a subset of UnlinkedItemCount: only items that actually carried a score
// needed one cleared. Both are reported because they answer different questions — how much was
// unnormalized, and how much stale scoring was purged.
public record RequeuePendingResult(
	int DeletedDescriptionCount,
	int UnlinkedItemCount,
	int ClearedMatchScoreCount);
