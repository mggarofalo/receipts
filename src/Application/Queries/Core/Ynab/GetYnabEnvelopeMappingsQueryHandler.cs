using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabEnvelopeMappingsQueryHandler(
	IYnabEnvelopeMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetYnabEnvelopeMappingsQuery, List<YnabEnvelopeMappingDto>>
{
	public async ValueTask<List<YnabEnvelopeMappingDto>> Handle(GetYnabEnvelopeMappingsQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return string.IsNullOrEmpty(budgetId) ? [] : await service.GetByBudgetIdAsync(budgetId, cancellationToken);
	}
}
