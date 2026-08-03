using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetItemCostOverTimeQuery(
	string? Description,
	string? Category,
	DateOnly? StartDate,
	DateOnly? EndDate,
	string Granularity,
	string? NormalizedDescription = null) : IQuery<ItemCostOverTimeResult>;
