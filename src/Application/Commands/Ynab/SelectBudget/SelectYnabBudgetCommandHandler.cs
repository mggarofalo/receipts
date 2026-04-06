using Application.Interfaces.Services;
using MediatR;

namespace Application.Commands.Ynab.SelectBudget;

public class SelectYnabBudgetCommandHandler(IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<SelectYnabBudgetCommand, Unit>
{
	public async Task<Unit> Handle(SelectYnabBudgetCommand request, CancellationToken cancellationToken)
	{
		await budgetSelectionService.SetSelectedBudgetIdAsync(request.BudgetId, cancellationToken);
		return Unit.Value;
	}
}
