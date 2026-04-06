using Application.Interfaces.Services;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabBudgetSelectionService(IYnabBudgetSelectionRepository repository) : IYnabBudgetSelectionService
{
	public Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken)
		=> repository.GetSelectedBudgetIdAsync(cancellationToken);

	public Task SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken)
		=> repository.SetSelectedBudgetIdAsync(budgetId, cancellationToken);
}
