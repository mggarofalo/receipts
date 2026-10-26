using Application.Models.Dashboard;
using Application.Models.Reports;

namespace Application.Interfaces.Services;

public interface IDashboardService
{
	Task<DashboardSummaryResult> GetSummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
	Task<SpendingOverTimeResult> GetSpendingOverTimeAsync(DateOnly startDate, DateOnly endDate, string granularity, CancellationToken cancellationToken);
	Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(DateOnly startDate, DateOnly endDate, int limit, CancellationToken cancellationToken);
	Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(DateOnly startDate, DateOnly endDate, int limit, CancellationToken cancellationToken, CategoryReportOptions options);
	Task<SpendingByAccountResult> GetSpendingByAccountAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
	Task<SpendingByStoreResult> GetSpendingByStoreAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
	Task<int> GetEarliestReceiptYearAsync(CancellationToken cancellationToken);
}
