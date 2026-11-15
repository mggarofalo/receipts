using Application.Models.Reports;
using Application.Queries.Aggregates.Dashboard;

namespace Application.Tests.Queries.Aggregates.Dashboard;

public class GetDashboardSummaryQueryTests : IQueryTests
{
	[Fact]
	public void Query_CanBeCreated()
	{
		GetDashboardSummaryQuery query = new(DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), DateOnly.FromDateTime(DateTime.Today), CategoryReportView.AsClassified);
		Assert.NotNull(query);
	}
}
