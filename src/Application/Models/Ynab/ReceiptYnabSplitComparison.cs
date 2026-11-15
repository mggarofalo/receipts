namespace Application.Models.Ynab;

/// <summary>
/// Result of comparing the expected YNAB category split (computed locally from
/// <see cref="Infrastructure.Services.YnabSplitCalculator"/>) against the actual state
/// currently stored in YNAB for a receipt's pushed transactions.
/// </summary>
public record ReceiptYnabSplitComparisonResult(
	bool CanComputeExpected,
	string? ExpectedUnavailableReason,
	List<string> UnmappedCategories,
	List<TransactionSplitComparison> TransactionComparisons);

public record TransactionSplitComparison(
	Guid LocalTransactionId,
	string AccountName,
	long TotalMilliunits,
	List<SplitLine> Expected,
	List<SplitLine>? Actual,
	string? ActualFetchError,
	bool? Matches);

public record SplitLine(string YnabCategoryId, string CategoryName, long Milliunits);
