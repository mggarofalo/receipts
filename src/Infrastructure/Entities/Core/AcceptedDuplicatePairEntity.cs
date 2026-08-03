using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Core;

/// <summary>
/// A user assertion that two receipts are genuinely separate purchases, not a double entry
/// (RECEIPTS-834). The duplicate-detection report suppresses a computed group when every
/// unordered pair inside it has a row here.
///
/// The relation is deliberately PAIRWISE rather than keyed on the whole group. Two other keys
/// were considered and rejected:
///   - <c>DuplicateGroup.MatchKey</c> is a formatted display string built from the group's first
///     receipt and, in total-clustered modes, an amount rendered with the CURRENT tolerance
///     setting. Keying on it means the dismissal silently stops matching the moment a user
///     changes tolerance or location normalization.
///   - The sorted set of receipt GUIDs is stable against tolerance changes, but brittle against
///     membership changes: a group that later gains or loses one receipt hashes differently and
///     the dismissal stops applying entirely.
/// Pairwise degrades the way a user would expect. A group that loses a member keeps every
/// remaining pair dismissed, so it stays quiet. A group that gains a member has undismissed pairs,
/// so it resurfaces — which is correct, because the newcomer has never been reviewed.
///
/// The cost is O(n^2) rows per accepted group; duplicate groups are two to a handful of receipts,
/// so the row count stays trivial.
/// </summary>
public class AcceptedDuplicatePairEntity : ISoftDeletable
{
	public Guid Id { get; set; }

	/// <summary>Lower of the two receipt IDs. The canonical ordering is enforced by a check constraint.</summary>
	public Guid ReceiptIdA { get; set; }

	/// <summary>Higher of the two receipt IDs.</summary>
	public Guid ReceiptIdB { get; set; }

	public DateTimeOffset AcceptedAt { get; set; }

	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }

	/// <summary>
	/// Always null. Present to satisfy <see cref="ISoftDeletable"/>; this entity deliberately does
	/// NOT implement <c>IOwnedBy&lt;ReceiptEntity&gt;</c>, so soft-deleting a receipt leaves the
	/// acceptance intact and restoring the receipt brings back a still-suppressed group.
	/// </summary>
	public Guid? CascadeDeletedByParentId { get; set; }
}
