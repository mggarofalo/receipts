namespace Application.Models.NormalizedDescriptions;

// Read-only projection of what INormalizedDescriptionService.RequeuePendingAsync would destroy
// (RECEIPTS-883). Existing PendingReview rows predate near-miss capture, so they render
// "no comparison recorded" forever; the fix is to delete and let the resolver rebuild them with
// full evidence rather than backfill a neighbour against today's registry.
//
// The same projection doubles as the post-run verification. After a successful requeue every
// count reads zero — in particular StaleMatchScoreCount, which is the checklist invariant that
// no receipt item is left with a null FK and a non-null match score.
public record RequeuePendingPreview(
	int PendingDescriptionCount,
	int LinkedItemCount,
	int StaleMatchScoreCount,
	int EstimatedResolverCycles,
	int EstimatedCatchUpSeconds);
