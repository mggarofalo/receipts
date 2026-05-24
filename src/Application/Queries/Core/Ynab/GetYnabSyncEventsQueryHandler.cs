using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabSyncEventsQueryHandler(IYnabSyncEventService syncEventService) : IRequestHandler<GetYnabSyncEventsQuery, YnabSyncEventsPage>
{
	public async ValueTask<YnabSyncEventsPage> Handle(GetYnabSyncEventsQuery request, CancellationToken cancellationToken)
	{
		return await syncEventService.ListAsync(request.Offset, request.Limit, request.Outcome, cancellationToken);
	}
}
