namespace Application.Queries.Core.ItemTemplate.GetSimilarItems;

public class SimilarItemResult
{
	public required string Name { get; set; }
	public double Similarity { get; set; }
	public double? SemanticSimilarity { get; set; }
	public double CombinedScore { get; set; }
	public required string Source { get; set; }
	public string? DefaultCategory { get; set; }
	public string? DefaultSubcategory { get; set; }
	public decimal? DefaultUnitPrice { get; set; }
	public string? DefaultItemCode { get; set; }

	/// <summary>
	/// True when only the semantic neighbours admitted this row, not the trigram threshold (RECEIPTS-1098).
	/// Such a row is not a typed completion, so it needs <see cref="Normalization.ReceiptSuggestionEvidence.SemanticFloor"/> instead.
	/// </summary>
	public bool AdmittedBySemanticSearchOnly { get; set; }
}
