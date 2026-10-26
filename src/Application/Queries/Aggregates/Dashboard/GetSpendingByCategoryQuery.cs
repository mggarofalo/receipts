using Application.Interfaces;
using Application.Models.Dashboard;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Dashboard;

public record GetSpendingByCategoryQuery(DateOnly StartDate, DateOnly EndDate, int Limit) : IQuery<SpendingByCategoryResult>
{
	public CategoryReportOptions? CategoryOptions { get; init; }
}
