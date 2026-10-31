using Application.Classification;

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
		string TotalAmountCurrency = "USD")
{
	public string Location { get; init; } = "";
	/// <summary>Jev's latest answer for the item, whatever its attempt state now; null when Jev never answered.</summary>
	public UncategorizedItemSuggestion? Suggestion { get; init; }
}

/// <summary>
/// A stored Jev answer: the item type it chose (null on abstention or an unreadable result) and its ranked
/// alternatives. Candidates are empty for results of earlier question policies.
/// </summary>
public record UncategorizedItemSuggestion(
	Guid AttemptId,
	string State,
	Guid? CategoryId,
	double? Confidence,
	string? AbstentionReason,
	IReadOnlyList<CategoryAssistanceCandidateRead> RootCandidates,
	IReadOnlyList<CategoryAssistanceCandidateRead> LeafCandidates);

public record UncategorizedItemsResult(
	List<UncategorizedItemRecord> Items,
	int TotalCount);
