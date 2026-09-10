using Application.Interfaces.Services;
using Application.Utilities;
using Mediator;

namespace Application.Commands.Ynab.CategoryMapping;

public class UpdateYnabCategoryMappingCommandHandler(
	IYnabCategoryMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<UpdateYnabCategoryMappingCommand, Unit>
{
	public async ValueTask<Unit> Handle(UpdateYnabCategoryMappingCommand request, CancellationToken cancellationToken)
	{
		string budgetId = YnabDestinationId.Canonicalize(request.YnabBudgetId);
		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (!string.Equals(budgetId, selectedBudgetId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The mapping budget must match the selected YNAB budget.", nameof(request));
		}

		await service.UpdateAsync(
			request.Id,
			request.YnabCategoryId,
			request.YnabCategoryName,
			request.YnabCategoryGroupName,
			budgetId,
			cancellationToken);

		return Unit.Value;
	}
}
