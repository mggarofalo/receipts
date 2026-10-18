namespace Application.Models.NormalizedDescriptions;

// Result of INormalizedDescriptionService.TestMatchAsync — the admin-facing "what would happen
// if I ran GetOrCreate on this description" probe. Candidates are the top-N ANN matches; the
// simulated outcome tells the admin which branch (auto-accept / pending review / create new)
// the production resolver would take given the supplied overrides or the live DB thresholds.
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
