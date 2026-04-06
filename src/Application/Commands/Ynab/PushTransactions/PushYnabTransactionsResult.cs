namespace Application.Commands.Ynab.PushTransactions;

public record PushYnabTransactionsResult(
	bool Success,
	List<PushedTransactionInfo> PushedTransactions,
	List<string>? UnmappedCategories = null,
	string? Error = null);

public record PushedTransactionInfo(
	Guid LocalTransactionId,
	string YnabTransactionId,
	long Milliunits,
	int SubTransactionCount);
