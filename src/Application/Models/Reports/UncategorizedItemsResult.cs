namespace Application.Models.Reports;

public record UncategorizedItemRecord(
	Guid Id,
	Guid ReceiptId,
	string? ReceiptItemCode,
	string Description,
	decimal Quantity,
	decimal UnitPrice,
	decimal TotalAmount,
	string Category,
		string? Subcategory,
		string ClassificationState = "missing_assignment",
		Guid? AssignmentId = null,
		Guid? CategoryId = null,
		Guid? TaxonomyRevisionId = null,
		string TotalAmountCurrency = "USD");

public record UncategorizedItemsResult(
	List<UncategorizedItemRecord> Items,
	int TotalCount);
