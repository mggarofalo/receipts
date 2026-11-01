using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetItemCostOverTimeQuery(
	string? Description,
	DateOnly? StartDate,
	DateOnly? EndDate,
	string Granularity,
		string? NormalizedDescription = null) : IQuery<ItemCostOverTimeResult>
{
	public CategoryReportOptions CategoryOptions { get; init; } = new();
}
