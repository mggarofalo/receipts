namespace Application.Models.Ynab;

public record YnabSubTransaction(long Amount, string CategoryId, string? Memo);

// ImportId is the immutable local operation key retained for snapshot compatibility.
// The transport writes it into the memo, never into YNAB's import_id field.
public record YnabCreateTransactionRequest(
	string AccountId,
	DateOnly Date,
	long Amount,
	string? Memo,
	string? PayeeName,
	string? CategoryId,
	bool Approved,
	List<YnabSubTransaction>? SubTransactions = null,
	string? ImportId = null);
