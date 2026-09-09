using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabBudgetSelectionService(
	IYnabBudgetSelectionRepository repository,
	ICommittedChangePublisher committedChangePublisher) : IYnabBudgetSelectionService
{
	public YnabBudgetSelectionService(IYnabBudgetSelectionRepository repository)
		: this(repository, new NullCommittedChangePublisher())
	{
	}

	public Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken)
		=> repository.GetSelectedBudgetIdAsync(cancellationToken);

	public async Task SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken)
	{
		if (await repository.SetSelectedBudgetIdAsync(budgetId, cancellationToken))
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabBudget,
				CommittedChangeType.Updated));
		}
	}
}
