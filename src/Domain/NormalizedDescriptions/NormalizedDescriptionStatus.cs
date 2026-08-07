namespace Domain.NormalizedDescriptions;

public enum NormalizedDescriptionStatus
{
	Active,
	PendingReview,

	/// <summary>
	/// A remembered "no" (RECEIPTS-876). The reviewer judged this raw text not worth a canonical
	/// entry, and the row survives as a tombstone so the resolver will not recreate it the next
	/// time the same text arrives. Its receipt items fall back to unnormalized.
	/// </summary>
	/// <remarks>
	/// Distinct from Merge, which says "this is the same as X". Rejected says "this is garbage
	/// text, stop asking me" — a disposition merge cannot express, since merging would silently
	/// re-point the items at an unrelated canonical row.
	/// </remarks>
	Rejected,
}
