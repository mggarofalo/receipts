using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetUnmappedCategoriesQueryHandler(
	IYnabCategoryMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetUnmappedCategoriesQuery, List<string>>
{
	public async ValueTask<List<string>> Handle(GetUnmappedCategoriesQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return string.IsNullOrEmpty(budgetId)
			? await service.GetDistinctReceiptItemCategoriesAsync(cancellationToken)
			: await service.GetUnmappedCategoriesAsync(budgetId, cancellationToken);
	}
}
