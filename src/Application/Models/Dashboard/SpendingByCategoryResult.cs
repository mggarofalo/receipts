using Application.Models.Reports;

namespace Application.Models.Dashboard;

public record SpendingByCategoryResult(List<SpendingCategoryItemResult> Items)
{
	public CategoryReportResult? Snapshot { get; init; }
}

public record SpendingCategoryItemResult(string CategoryName, decimal Amount, decimal Percentage)
{
	public CategoryReportBucket? Bucket { get; init; }
}
