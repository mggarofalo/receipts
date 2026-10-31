using Application.Models.Reports;

namespace Application.Interfaces.Services;

public interface IReportService
{
	Task<OutOfBalanceResult> GetOutOfBalanceAsync(
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken);

	Task<SpendingByLocationResult> GetSpendingByLocationAsync(
			DateOnly? startDate,
			DateOnly? endDate,
			string sortBy,
			string sortDirection,
			int page,
			int pageSize,
			CancellationToken cancellationToken);

	Task<ItemDescriptionResult> GetItemDescriptionsAsync(
		string search,
		bool categoryOnly,
		int limit,
		CancellationToken cancellationToken);

	Task<ItemCostOverTimeResult> GetItemCostOverTimeAsync(
		string? description,
		string? category,
		DateOnly? startDate,
		DateOnly? endDate,
		string granularity,
		string? normalizedDescription,
		CancellationToken cancellationToken);
	Task<ItemCostOverTimeResult> GetItemCostOverTimeAsync(
			string? description, string? category, DateOnly? startDate, DateOnly? endDate,
			string granularity, string? normalizedDescription, CancellationToken cancellationToken,
			CategoryReportOptions options);

	Task<CategoryTrendsResult> GetCategoryTrendsAsync(
		DateOnly startDate,
		DateOnly endDate,
		string granularity,
		int topN,
		CancellationToken cancellationToken);
	Task<CategoryTrendsResult> GetCategoryTrendsAsync(
		DateOnly startDate,
		DateOnly endDate,
		string granularity,
		int topN,
		CancellationToken cancellationToken, CategoryReportOptions options);

	/// <param name="suggestionFilter">any, with (Jev has answered for the item) or without.</param>
	Task<UncategorizedItemsResult> GetUncategorizedItemsAsync(
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		string suggestionFilter,
		CancellationToken cancellationToken);

	Task<SpendingByNormalizedDescriptionResult> GetSpendingByNormalizedDescriptionAsync(
		DateTimeOffset? from,
		DateTimeOffset? to,
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken);

	Task<ReportsHealthSummaryResult> GetHealthSummaryAsync(CancellationToken cancellationToken);
}
