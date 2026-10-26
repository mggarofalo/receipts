using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetCategoryTrendsReportQuery(
	DateOnly StartDate,
	DateOnly EndDate,
	string Granularity,
	int TopN) : IQuery<CategoryTrendsResult>
{
	public CategoryReportOptions? CategoryOptions { get; init; }
}
