using Application.Interfaces.Services;
using Application.Utilities;
using Mediator;

namespace Application.Commands.Ynab.AccountMapping;

public class UpdateYnabAccountMappingCommandHandler(
	IYnabAccountMappingService accountMappingService,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<UpdateYnabAccountMappingCommand, Unit>
{
	public async ValueTask<Unit> Handle(UpdateYnabAccountMappingCommand request, CancellationToken cancellationToken)
	{
		string budgetId = YnabDestinationId.Canonicalize(request.YnabBudgetId);
		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (!string.Equals(budgetId, selectedBudgetId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The mapping budget must match the selected YNAB budget.", nameof(request));
		}

		await accountMappingService.UpdateAsync(
			request.Id,
			request.YnabAccountId,
			request.YnabAccountName,
			budgetId,
			cancellationToken);
		return Unit.Value;
	}
}
