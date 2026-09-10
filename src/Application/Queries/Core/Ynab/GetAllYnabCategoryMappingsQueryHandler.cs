using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetAllYnabCategoryMappingsQueryHandler(
	IYnabCategoryMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetAllYnabCategoryMappingsQuery, List<YnabCategoryMappingDto>>
{
	public async ValueTask<List<YnabCategoryMappingDto>> Handle(GetAllYnabCategoryMappingsQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			return [];
		}

		return await service.GetByBudgetIdAsync(budgetId, cancellationToken);
	}
}
