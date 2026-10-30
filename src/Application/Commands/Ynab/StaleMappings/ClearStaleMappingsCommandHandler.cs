using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Commands.Ynab.StaleMappings;

public class ClearStaleMappingsCommandHandler(
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabAccountMappingService accountMappingService,
	IYnabEnvelopeMappingService envelopeMappingService) : IRequestHandler<ClearStaleMappingsCommand, ClearStaleMappingsResult>
{
	public async ValueTask<ClearStaleMappingsResult> Handle(ClearStaleMappingsCommand request, CancellationToken cancellationToken)
	{
		string? currentBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);

		if (string.IsNullOrEmpty(currentBudgetId))
		{
			return new ClearStaleMappingsResult(0, 0);
		}

		int deletedAccounts = await accountMappingService.DeleteStaleMappingsAsync(currentBudgetId, cancellationToken);
		int deletedCategories = await envelopeMappingService.DeleteStaleMappingsAsync(currentBudgetId, cancellationToken);

		return new ClearStaleMappingsResult(deletedAccounts, deletedCategories);
	}
}
