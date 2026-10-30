using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetStaleMappingsQueryHandler(
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabAccountMappingService accountMappingService,
	IYnabEnvelopeMappingService envelopeMappingService) : IRequestHandler<GetStaleMappingsQuery, StaleMappingsResult>
{
	public async ValueTask<StaleMappingsResult> Handle(GetStaleMappingsQuery request, CancellationToken cancellationToken)
	{
		string? currentBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);

		if (string.IsNullOrEmpty(currentBudgetId))
		{
			return new StaleMappingsResult(0, 0, currentBudgetId);
		}

		int staleAccountCount = await accountMappingService.CountStaleMappingsAsync(currentBudgetId, cancellationToken);
		int staleCategoryCount = await envelopeMappingService.CountStaleMappingsAsync(currentBudgetId, cancellationToken);

		return new StaleMappingsResult(staleAccountCount, staleCategoryCount, currentBudgetId);
	}
}
