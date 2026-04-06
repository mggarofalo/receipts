using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.StaleMappings;

public class ClearStaleMappingsCommandHandler(
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabAccountMappingService accountMappingService,
	IYnabCategoryMappingService categoryMappingService) : IRequestHandler<ClearStaleMappingsCommand, ClearStaleMappingsResult>
{
	public async Task<ClearStaleMappingsResult> Handle(ClearStaleMappingsCommand request, CancellationToken cancellationToken)
	{
		string? currentBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);

		if (string.IsNullOrEmpty(currentBudgetId))
		{
			return new ClearStaleMappingsResult(0, 0);
		}

		int deletedAccounts = await accountMappingService.DeleteStaleMappingsAsync(currentBudgetId, cancellationToken);
		int deletedCategories = await categoryMappingService.DeleteStaleMappingsAsync(currentBudgetId, cancellationToken);

		return new ClearStaleMappingsResult(deletedAccounts, deletedCategories);
	}
}
