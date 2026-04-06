namespace Application.Models.Ynab;

public record YnabSubTransaction(long Amount, string CategoryId, string? Memo);

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
