namespace Application.Models.Reports;

public record CategoryTrendsBucketResult(string Period, List<decimal> Amounts);
public record CategoryTrendsSeriesResult(string Currency, bool IsOther, CategoryReportBucket? Bucket);
public record CategoryTrendsResult(List<string> Categories, List<CategoryTrendsBucketResult> Buckets)
{
	public CategoryReportResult? Snapshot { get; init; }
	public List<CategoryTrendsSeriesResult> Series { get; init; } = [];
}
