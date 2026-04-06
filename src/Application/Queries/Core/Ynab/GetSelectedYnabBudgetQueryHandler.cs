using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetSelectedYnabBudgetQueryHandler(IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetSelectedYnabBudgetQuery, YnabBudgetSelection>
{
	public async Task<YnabBudgetSelection> Handle(GetSelectedYnabBudgetQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return new YnabBudgetSelection(budgetId);
	}
}
