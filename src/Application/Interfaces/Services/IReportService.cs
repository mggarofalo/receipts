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
		CancellationToken cancellationToken);

	Task<DuplicateDetectionResult> GetDuplicatesAsync(
		string matchOn,
		string locationTolerance,
		decimal totalTolerance,
		CancellationToken cancellationToken);

	Task<CategoryTrendsResult> GetCategoryTrendsAsync(
		DateOnly startDate,
		DateOnly endDate,
		string granularity,
		int topN,
		CancellationToken cancellationToken);

	Task<UncategorizedItemsResult> GetUncategorizedItemsAsync(
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken);

	Task<SpendingByNormalizedDescriptionResult> GetSpendingByNormalizedDescriptionAsync(
		DateTimeOffset? from,
		DateTimeOffset? to,
		CancellationToken cancellationToken);

	Task<ReportsHealthSummaryResult> GetHealthSummaryAsync(CancellationToken cancellationToken);
}
