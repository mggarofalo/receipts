using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetCategoryTrendsReportQueryHandler(IReportService reportService)
	: IRequestHandler<GetCategoryTrendsReportQuery, CategoryTrendsResult>
{
	public async ValueTask<CategoryTrendsResult> Handle(GetCategoryTrendsReportQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetCategoryTrendsAsync(
			request.StartDate,
			request.EndDate,
			request.Granularity,
			request.TopN,
			cancellationToken, request.CategoryOptions ?? new CategoryReportOptions());
	}
}
