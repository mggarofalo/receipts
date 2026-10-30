using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Utilities;
using Mediator;

namespace Application.Commands.Ynab.EnvelopeMapping;

public class SetYnabEnvelopeMappingCommandHandler(
	IYnabEnvelopeMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<SetYnabEnvelopeMappingCommand, YnabEnvelopeMappingDto>
{
	public async ValueTask<YnabEnvelopeMappingDto> Handle(SetYnabEnvelopeMappingCommand request, CancellationToken cancellationToken)
	{
		string budgetId = YnabDestinationId.Canonicalize(request.YnabBudgetId);
		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (!string.Equals(budgetId, selectedBudgetId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The mapping budget must match the selected YNAB budget.", nameof(request));
		}

		return await service.SetAsync(request.EnvelopeId, budgetId, request.YnabCategoryId,
			request.YnabCategoryName, request.YnabCategoryGroupName, cancellationToken);
	}
}
