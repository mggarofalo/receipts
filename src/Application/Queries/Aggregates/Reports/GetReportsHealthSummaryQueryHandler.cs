using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetReportsHealthSummaryQueryHandler(IReportService reportService)
	: IRequestHandler<GetReportsHealthSummaryQuery, ReportsHealthSummaryResult>
{
	public async ValueTask<ReportsHealthSummaryResult> Handle(GetReportsHealthSummaryQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetHealthSummaryAsync(cancellationToken);
	}
}
