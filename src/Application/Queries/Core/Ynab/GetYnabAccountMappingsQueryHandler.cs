using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabAccountMappingsQueryHandler(
	IYnabAccountMappingService accountMappingService,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetYnabAccountMappingsQuery, List<YnabAccountMappingDto>>
{
	public async ValueTask<List<YnabAccountMappingDto>> Handle(GetYnabAccountMappingsQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return [];
		}

		return await accountMappingService.GetByBudgetIdAsync(budgetId, cancellationToken);
	}
}
