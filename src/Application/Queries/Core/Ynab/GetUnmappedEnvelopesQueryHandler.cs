using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetUnmappedEnvelopesQueryHandler(
	IYnabEnvelopeMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetUnmappedEnvelopesQuery, List<Domain.Envelopes.Envelope>>
{
	public async ValueTask<List<Domain.Envelopes.Envelope>> Handle(GetUnmappedEnvelopesQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return string.IsNullOrEmpty(budgetId) ? [] : await service.GetUnmappedEnvelopesAsync(budgetId, cancellationToken);
	}
}
