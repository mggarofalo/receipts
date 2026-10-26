using Application.Models.Reports;

namespace Application.Models.Search;

/// <summary>Validated whole-result filters and ordering, independent of transport and paging.</summary>
public record PurchaseSearchOptions(
	string SortBy = "date",
	string SortDirection = "desc",
	string? Location = null,
	DateOnly? StartDate = null,
	DateOnly? EndDate = null,
	decimal? MinUnitPrice = null,
	decimal? MaxUnitPrice = null,
	bool IncludeUnreviewed = false,
		Guid? NormalizedDescriptionId = null,
		CategoryReportView CategoryView = CategoryReportView.AsClassified,
		Guid? CategoryId = null,
		DateTimeOffset? KnowledgeCutoff = null,
		Guid? CapturedCurrentRevisionId = null);
