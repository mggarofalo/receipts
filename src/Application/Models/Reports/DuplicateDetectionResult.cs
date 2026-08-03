namespace Application.Models.Reports;

public record DuplicateReceiptSummary(
	Guid ReceiptId,
	string Location,
	DateOnly Date,
	decimal TransactionTotal);

/// <param name="MatchKey">
/// Human-readable description of what these receipts have in common. This is a DISPLAY string —
/// it is derived from the group's first receipt and, in total-clustered modes, from an amount
/// rendered with the current tolerance setting. It is not a stable identity and must never be
/// used as a persistence key (RECEIPTS-834).
/// </param>
/// <param name="IsAccepted">
/// True when every unordered pair of receipts in this group has been accepted as "not a
/// duplicate". Accepted groups are omitted from the report unless explicitly included.
/// </param>
public record DuplicateGroup(
	string MatchKey,
	List<DuplicateReceiptSummary> Receipts,
	bool IsAccepted = false);

public record DuplicateDetectionResult(
	List<DuplicateGroup> Groups,
	int GroupCount,
	int TotalDuplicateReceipts);

/// <summary>
/// A set of receipts a user has declared genuinely separate. Derived from the connected components
/// of the accepted-pair graph, so two acceptances that share a receipt surface as one group.
/// </summary>
/// <param name="Receipts">
/// The members that still exist, hydrated for display. A member whose receipt was soft-deleted or
/// purged is omitted — there is nothing to show and nothing left to warn about.
/// </param>
/// <param name="MemberReceiptIds">
/// EVERY member of the component, including the ones missing from <paramref name="Receipts"/>.
/// Undo must submit this, not the displayed subset: un-accepting only the survivors would leave the
/// pairs that touch a deleted member stored forever, and no client-producible set could reach them
/// again. Kept separate from the displayed list so the fix does not depend on rendering rows for
/// receipts the user can no longer see.
/// </param>
public record AcceptedDuplicateGroup(
	List<DuplicateReceiptSummary> Receipts,
	List<Guid> MemberReceiptIds,
	DateTimeOffset AcceptedAt);

public record AcceptedDuplicatesResult(
	List<AcceptedDuplicateGroup> Groups,
	int GroupCount);
