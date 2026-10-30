using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Ynab.EnvelopeMapping;

public class DeleteYnabEnvelopeMappingCommandHandler(
	IYnabEnvelopeMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<DeleteYnabEnvelopeMappingCommand, bool>
{
	public async ValueTask<bool> Handle(DeleteYnabEnvelopeMappingCommand request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return !string.IsNullOrEmpty(budgetId) && await service.DeleteAsync(request.EnvelopeId, budgetId, cancellationToken);
	}
}
