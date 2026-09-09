namespace Infrastructure.Interfaces.Repositories;

public interface IYnabBudgetSelectionRepository
{
	Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken);
	Task<bool> SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken);
}
