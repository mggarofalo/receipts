using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabApiClient
{
	Task<List<YnabBudget>> GetBudgetsAsync(CancellationToken cancellationToken);
	Task<List<YnabAccount>> GetAccountsAsync(string budgetId, CancellationToken cancellationToken);
	Task<List<YnabCategory>> GetCategoriesAsync(string budgetId, CancellationToken cancellationToken);
	Task<YnabTransaction?> GetTransactionAsync(string budgetId, string transactionId, CancellationToken cancellationToken);
	Task<YnabCreateTransactionResponse> CreateTransactionAsync(string budgetId, YnabCreateTransactionRequest request, CancellationToken cancellationToken);
	Task<List<YnabTransaction>> GetTransactionsByDateAsync(string budgetId, DateOnly sinceDate, CancellationToken cancellationToken);
	Task UpdateTransactionMemoAsync(string budgetId, string transactionId, string memo, CancellationToken cancellationToken);
	bool IsConfigured { get; }
}
