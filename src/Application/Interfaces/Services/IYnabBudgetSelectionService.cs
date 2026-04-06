namespace Application.Interfaces.Services;

public interface IYnabBudgetSelectionService
{
	Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken);
	Task SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken);
}
