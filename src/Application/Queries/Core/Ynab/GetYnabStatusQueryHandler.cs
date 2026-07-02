using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabStatusQueryHandler(
	IYnabApiClient ynabApiClient,
	IYnabSyncEventService ynabSyncEventService) : IRequestHandler<GetYnabStatusQuery, YnabStatus>
{
	public async ValueTask<YnabStatus> Handle(GetYnabStatusQuery request, CancellationToken cancellationToken)
	{
		return await ynabSyncEventService.GetStatusAsync(ynabApiClient.IsConfigured, cancellationToken);
	}
}
