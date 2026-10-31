using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetUncategorizedItemsReportQuery(
	string SortBy,
	string SortDirection,
	int Page,
	int PageSize,
	string SuggestionFilter = "any") : IQuery<UncategorizedItemsResult>;
