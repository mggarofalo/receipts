namespace Application.Normalization;

public enum MatchDecision
{
	ReuseExact,
	Rejected,
	ReuseSimilar,
	CreatePendingReview,
	CreateNew,
	EmbeddingUnavailable,
}
