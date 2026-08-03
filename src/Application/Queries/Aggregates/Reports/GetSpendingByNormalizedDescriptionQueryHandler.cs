using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetSpendingByNormalizedDescriptionQueryHandler(IReportService reportService)
	: IRequestHandler<GetSpendingByNormalizedDescriptionQuery, SpendingByNormalizedDescriptionResult>
{
	public async ValueTask<SpendingByNormalizedDescriptionResult> Handle(GetSpendingByNormalizedDescriptionQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetSpendingByNormalizedDescriptionAsync(
			request.From,
			request.To,
			request.SortBy,
			request.SortDirection,
			request.Page,
			request.PageSize,
			cancellationToken);
	}
}
