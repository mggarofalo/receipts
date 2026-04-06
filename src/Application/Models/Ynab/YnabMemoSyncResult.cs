namespace Application.Models.Ynab;

/// <summary>
/// Result of a memo sync attempt for a single local transaction.
/// </summary>
public record YnabMemoSyncResult(
	Guid LocalTransactionId,
	Guid ReceiptId,
	YnabMemoSyncOutcome Outcome,
	string? YnabTransactionId,
	string? Error,
	List<YnabTransactionCandidate>? AmbiguousCandidates);

/// <summary>
/// A YNAB transaction candidate shown to the user for disambiguation.
/// </summary>
public record YnabTransactionCandidate(
	string Id,
	DateOnly Date,
	long Amount,
	string? Memo,
	string? PayeeName,
	string AccountId);

public enum YnabMemoSyncOutcome
{
	/// <summary>Memo successfully updated on YNAB transaction.</summary>
	Synced,

	/// <summary>Memo already contains the receipt link — no update needed.</summary>
	AlreadySynced,

	/// <summary>No matching YNAB transaction found for the local transaction.</summary>
	NoMatch,

	/// <summary>Multiple YNAB transactions match — user must choose.</summary>
	Ambiguous,

	/// <summary>Transaction currency is not USD — skipped for V1.</summary>
	CurrencySkipped,

	/// <summary>An error occurred during sync.</summary>
	Failed
}
