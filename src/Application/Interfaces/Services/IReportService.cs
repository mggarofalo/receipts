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

	Task<DuplicateDetectionResult> GetDuplicatesAsync(
		string matchOn,
		string locationTolerance,
		decimal totalTolerance,
		bool includeAccepted,
		CancellationToken cancellationToken);

	/// <summary>
	/// Records every unordered pair of <paramref name="receiptIds"/> as "not a duplicate". Idempotent:
	/// pairs already accepted are left alone, previously un-accepted pairs are restored.
	/// </summary>
	/// <returns>The number of pairs newly accepted (inserted or restored).</returns>
	/// <exception cref="KeyNotFoundException">One or more receipt IDs do not resolve to an active receipt.</exception>
	Task<int> AcceptDuplicateGroupAsync(
		List<Guid> receiptIds,
		CancellationToken cancellationToken);

	/// <summary>
	/// Removes the "not a duplicate" assertion for every unordered pair of <paramref name="receiptIds"/>.
	/// </summary>
	/// <returns>The number of accepted pairs removed.</returns>
	Task<int> UnacceptDuplicateGroupAsync(
		List<Guid> receiptIds,
		CancellationToken cancellationToken);

	/// <summary>
	/// Lists accepted duplicate groups — the connected components of the accepted-pair graph,
	/// hydrated with the receipts that are still active.
	/// </summary>
	Task<AcceptedDuplicatesResult> GetAcceptedDuplicatesAsync(
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
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken);

	Task<ReportsHealthSummaryResult> GetHealthSummaryAsync(CancellationToken cancellationToken);
}
