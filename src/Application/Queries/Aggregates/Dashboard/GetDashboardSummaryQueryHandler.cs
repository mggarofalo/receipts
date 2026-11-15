using Application.Interfaces.Services;
using Application.Models.Dashboard;
using Mediator;

namespace Application.Queries.Aggregates.Dashboard;

public class GetDashboardSummaryQueryHandler(IDashboardService dashboardService)
	: IRequestHandler<GetDashboardSummaryQuery, DashboardSummaryResult>
{
	public async ValueTask<DashboardSummaryResult> Handle(GetDashboardSummaryQuery request, CancellationToken cancellationToken)
	{
		return await dashboardService.GetSummaryAsync(request.StartDate, request.EndDate, request.CategoryView, cancellationToken);
	}
}
