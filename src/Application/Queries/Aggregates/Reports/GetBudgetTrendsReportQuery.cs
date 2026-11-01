using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetBudgetTrendsReportQuery(
	DateOnly StartDate,
	DateOnly EndDate,
	string Granularity,
	int TopN,
	BudgetTrendsDimension Dimension,
	Guid? EnvelopeId = null,
	Guid? OccasionId = null) : IQuery<BudgetTrendsResult>;
