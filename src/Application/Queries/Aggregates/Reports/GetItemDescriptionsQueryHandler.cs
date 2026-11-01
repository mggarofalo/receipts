using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetItemDescriptionsQueryHandler(IReportService reportService)
	: IRequestHandler<GetItemDescriptionsQuery, ItemDescriptionResult>
{
	public async ValueTask<ItemDescriptionResult> Handle(GetItemDescriptionsQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetItemDescriptionsAsync(
			request.Search,
			request.Limit,
			cancellationToken);
	}
}
