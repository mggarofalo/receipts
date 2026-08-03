namespace Application.Models.Reports;

public record SpendingByNormalizedDescriptionItem(
	string CanonicalName,
	decimal TotalAmount,
	string Currency,
	int ItemCount,
	DateTimeOffset? FirstSeen,
	DateTimeOffset? LastSeen);

public record SpendingByNormalizedDescriptionResult(
	List<SpendingByNormalizedDescriptionItem> Items,
	int TotalCount,
	decimal GrandTotal,
	DateTimeOffset? FromDate,
	DateTimeOffset? ToDate);
