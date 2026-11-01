namespace Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;

public class ReceiptItemSuggestion
{
	public required string ItemCode { get; set; }
	public required string Description { get; set; }
	/// <summary>Top level of the latest item's item type; empty when it has none.</summary>
	public required string Category { get; set; }
	public string? Subcategory { get; set; }
	/// <summary>The latest item's item type; null when it has none.</summary>
	public Guid? CategoryId { get; set; }
	public decimal UnitPrice { get; set; }
	public required string MatchType { get; set; }
}
