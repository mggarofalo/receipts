using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetStaleMappingsQueryHandler(
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabAccountMappingService accountMappingService,
	IYnabCategoryMappingService categoryMappingService) : IRequestHandler<GetStaleMappingsQuery, StaleMappingsResult>
{
	public async Task<StaleMappingsResult> Handle(GetStaleMappingsQuery request, CancellationToken cancellationToken)
	{
		string? currentBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);

		if (string.IsNullOrEmpty(currentBudgetId))
		{
			return new StaleMappingsResult(0, 0, currentBudgetId);
		}

		int staleAccountCount = await accountMappingService.CountStaleMappingsAsync(currentBudgetId, cancellationToken);
		int staleCategoryCount = await categoryMappingService.CountStaleMappingsAsync(currentBudgetId, cancellationToken);

		return new StaleMappingsResult(staleAccountCount, staleCategoryCount, currentBudgetId);
	}
}
