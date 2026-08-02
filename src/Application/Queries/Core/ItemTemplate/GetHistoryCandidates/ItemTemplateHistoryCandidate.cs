namespace Application.Queries.Core.ItemTemplate.GetHistoryCandidates;

/// <summary>
/// A recurring receipt-item description that has no matching item template yet,
/// with defaults suggested from the purchase history behind it.
/// </summary>
public class ItemTemplateHistoryCandidate
{
	/// <summary>Description text as it appeared on the most recent receipt.</summary>
	public required string Name { get; set; }

	/// <summary>Number of active receipt items sharing this description (case-insensitive).</summary>
	public int OccurrenceCount { get; set; }

	/// <summary>Date of the most recent receipt containing this description.</summary>
	public DateOnly LastPurchasedAt { get; set; }

	/// <summary>Most frequently used category for this description.</summary>
	public string? SuggestedCategory { get; set; }

	/// <summary>Subcategory paired with the most frequently used category.</summary>
	public string? SuggestedSubcategory { get; set; }

	/// <summary>Unit price from the most recent receipt containing this description.</summary>
	public decimal? SuggestedUnitPrice { get; set; }

	/// <summary>Most frequently used non-null item code for this description.</summary>
	public string? SuggestedItemCode { get; set; }
}
