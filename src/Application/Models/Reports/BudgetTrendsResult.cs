namespace Application.Models.Reports;

/// <summary>What budget trends groups purchases by.</summary>
public enum BudgetTrendsDimension
{
	Envelope,
	Occasion,
}

/// <summary>
/// One series: an envelope or occasion (Id null for purchases with none), or the per-currency Other
/// series for the rest. Archived envelopes and occasions keep their history.
/// </summary>
public sealed record BudgetTrendsSeriesResult(Guid? Id, string Name, bool IsActive, string Currency, bool IsOther,
	decimal Total, int PurchaseCount);

/// <summary>Spending over time by recorded envelope or occasion. Bucket amounts parallel <see cref="Series"/>.</summary>
public sealed record BudgetTrendsResult(BudgetTrendsDimension Dimension, List<BudgetTrendsSeriesResult> Series,
	List<CategoryTrendsBucketResult> Buckets);
