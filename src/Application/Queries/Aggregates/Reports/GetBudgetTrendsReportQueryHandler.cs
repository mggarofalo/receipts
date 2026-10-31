using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetBudgetTrendsReportQueryHandler(IReportService reportService)
	: IRequestHandler<GetBudgetTrendsReportQuery, BudgetTrendsResult>
{
	public async ValueTask<BudgetTrendsResult> Handle(GetBudgetTrendsReportQuery request, CancellationToken cancellationToken)
		=> await reportService.GetBudgetTrendsAsync(request.StartDate, request.EndDate, request.Granularity, request.TopN,
			request.Dimension, request.EnvelopeId, request.OccasionId, cancellationToken);
}
