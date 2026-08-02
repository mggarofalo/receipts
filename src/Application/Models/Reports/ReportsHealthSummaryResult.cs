namespace Application.Models.Reports;

/// <summary>
/// Headline counts for the data-quality reports, used by the reports hub to show
/// whether any hygiene report currently needs attention.
/// </summary>
public record ReportsHealthSummaryResult(
	int OutOfBalanceCount,
	int DuplicateGroupCount,
	int UncategorizedItemCount);
