namespace Application.Models.NormalizedDescriptions;

// Read-only threshold simulation for administrative candidate inspection. Candidates are
// bounded ANN matches; legacy outcome names describe the configured advisory thresholds.
// Simulation neither verifies purchases nor authorizes automatic identity creation.
public record MatchTestResult(
	List<MatchCandidate> Candidates,
	string SimulatedOutcome,
	Guid? SimulatedTargetId);

// Simulated outcomes for TestMatchAsync. Kept as string constants so the wire format stays
// human-readable and stable across refactors.
public static class MatchTestOutcomes
{
	public const string Rejected = "Rejected";
	public const string AutoAccept = "AutoAccept";
	public const string PendingReview = "PendingReview";
	public const string CreateNew = "CreateNew";
	public const string EmbeddingUnavailable = "EmbeddingUnavailable";
}

public record MatchCandidate(
	Guid NormalizedDescriptionId,
	string CanonicalName,
	double CosineSimilarity,
	string Status);
