namespace Application.Commands.Ynab.PushTransactions;

public record PushYnabTransactionsResult(
	bool Success,
	List<PushedTransactionInfo> PushedTransactions,
	List<string>? UnmappedCategories = null,
	string? Error = null)
{
	public static PushYnabTransactionsResult Failure(string error) =>
		new(false, [], null, error);
}

public record PushedTransactionInfo(
	Guid LocalTransactionId,
	string YnabTransactionId,
	long Milliunits,
	int SubTransactionCount);
