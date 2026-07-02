using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabSyncEventsQueryHandler(IYnabSyncEventService ynabSyncEventService)
	: IRequestHandler<GetYnabSyncEventsQuery, PagedResult<YnabSyncEventDto>>
{
	public async ValueTask<PagedResult<YnabSyncEventDto>> Handle(GetYnabSyncEventsQuery request, CancellationToken cancellationToken)
	{
		return await ynabSyncEventService.GetRecentAsync(
			request.Offset,
			request.Limit,
			request.Sort,
			request.Success,
			request.DateFrom,
			request.DateTo,
			cancellationToken);
	}
}
