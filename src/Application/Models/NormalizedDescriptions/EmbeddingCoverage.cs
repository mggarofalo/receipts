namespace Application.Models.NormalizedDescriptions;

public sealed record EmbeddingCoverage(
	string Fingerprint,
	int CanonicalReady,
	int CanonicalTotal,
	int ItemReady,
	int ItemTotal)
{
	public int CanonicalPending => CanonicalTotal - CanonicalReady;
	public int ItemPending => ItemTotal - ItemReady;
	public bool IsComplete => CanonicalPending == 0 && ItemPending == 0;
}
