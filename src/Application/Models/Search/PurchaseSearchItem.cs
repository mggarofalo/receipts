using Application.Models.Reports;
using Domain.Taxonomy;

namespace Application.Models.Search;

public record PurchaseSearchItem(
	Guid Id,
	Guid ReceiptId,
	DateOnly Date,
	string Location,
	string Description,
	string? ReceiptItemCode,
	Guid? NormalizedDescriptionId,
	string? NormalizedDescription,
	decimal Quantity,
	decimal UnitPrice,
	string UnitPriceCurrency,
	decimal TotalAmount,
	string TotalAmountCurrency,
		string NormalizationReviewState = "needsReview")
{
	public Guid? PathRevisionId { get; init; }
	public Guid? CategoryId { get; init; }
	public CategoryReportState CategoryState { get; init; } = CategoryReportState.MissingAssignment;
	public IReadOnlyList<TaxonomyNode> CategoryPath { get; init; } = [];
}
