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
public record AcceptedDuplicateGroup(
	List<DuplicateReceiptSummary> Receipts,
	DateTimeOffset AcceptedAt);

public record AcceptedDuplicatesResult(
	List<AcceptedDuplicateGroup> Groups,
	int GroupCount);
