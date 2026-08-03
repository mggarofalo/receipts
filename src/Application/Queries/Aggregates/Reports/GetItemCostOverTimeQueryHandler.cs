using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetItemCostOverTimeQueryHandler(IReportService reportService)
	: IRequestHandler<GetItemCostOverTimeQuery, ItemCostOverTimeResult>
{
	public async ValueTask<ItemCostOverTimeResult> Handle(GetItemCostOverTimeQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetItemCostOverTimeAsync(
			request.Description,
			request.Category,
			request.StartDate,
			request.EndDate,
			request.Granularity,
			request.NormalizedDescription,
			cancellationToken);
	}
}
