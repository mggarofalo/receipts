namespace Infrastructure.Interfaces.Repositories;

public interface IYnabBudgetSelectionRepository
{
	Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken);
	Task SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken);
}
