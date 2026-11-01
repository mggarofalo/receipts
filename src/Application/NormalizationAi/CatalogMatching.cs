using Application.Classification;

namespace Application.NormalizationAi;

/// <summary>
/// One shortlisted catalog name. DisplayName is what a person sees (DisplayLabel, else CanonicalName), which the
/// database keeps unique case-insensitively. Approved examples are the candidate's approved source descriptions, each
/// marked same-scope when it shares the purchase's store and item code; usually there are none.
/// </summary>
public sealed record CatalogMatchCandidate(Guid Id, string DisplayName, IReadOnlyList<NormalizationApprovedExample> ApprovedExamples);

/// <summary>
/// What a person sees on the receipt line (the item text and, when known, the store) and the shortlist to choose from.
/// Pass every approved example, uncapped: the request builder chooses which are shown.
/// </summary>
public sealed record CatalogMatchRequest(string Description, string? Store, IReadOnlyList<CatalogMatchCandidate> Candidates)
{
	/// <summary>
	/// The same request with every approved example of <paramref name="description"/> removed from every candidate, so an
	/// evaluated source is never its own evidence (leave-one-out). Candidates and their order are unchanged.
	/// </summary>
	public CatalogMatchRequest WithoutExamplesOf(string description) => this with
	{
		Candidates = [.. Candidates.Select(c => c with
		{
			ApprovedExamples = [.. c.ApprovedExamples.Where(e => !string.Equals(e.Description, description, StringComparison.Ordinal))]
		})]
	};
}

/// <summary>A structurally parsed stage-1 answer: Q1 probabilities by candidate ID and the Q2 fit Noul, plus billed usage.</summary>
public sealed record CatalogStage1Judgment(string Model, Guid ChosenCandidateId, double Confidence,
	IReadOnlyDictionary<Guid, double> Probabilities, double Fit, long InputTokens, long OutputTokens);

/// <summary>A structurally parsed stage-2 answer: the Q3 same-variant Noul, plus billed usage.</summary>
public sealed record CatalogStage2Judgment(string Model, double Same, long InputTokens, long OutputTokens);

public enum CatalogMatchOutcome { MatchSuggestion, Review, NeedsNewName }

/// <summary>
/// The truth table's bands. The defaults are the provisional values in docs/jev-catalog-matching.md; changing them never
/// changes a request.
/// </summary>
public sealed record CatalogMatchThresholds(double NoFitAtOrBelow = 0.3, double FitGate = 0.7, double ChoiceFloor = 0.5,
	double DifferentAtOrBelow = 0.3, double SameAtOrAbove = 0.7);

/// <summary>One truth-table row: the outcome, its reason code (null for a match) and the candidate a match proposes.</summary>
public sealed record CatalogMatchDecision(CatalogMatchOutcome Outcome, string? Reason, Guid? CandidateId);

/// <summary>
/// Input rules, answer validation and the decision of <see cref="Version"/> (docs/jev-catalog-matching.md). The limits are
/// category assistance's, because both policies share one Jev connection and its per-request bounds.
/// </summary>
public static class CatalogMatchingPolicy
{
	public const string Version = "catalog-matching-questions-v1";
	public const string Model = CategorySuggestionPolicy.Model;
	public const int MaximumOptions = CategorySuggestionPolicy.MaximumOptions;
	public const int MaximumPayloadBytes = CategorySuggestionPolicy.MaximumPayloadBytes;
	public const int MaximumExamplesPerCandidate = 3;

	/// <summary>Truth-table row 0: an empty shortlist goes to naming without asking Jev.</summary>
	public static readonly CatalogMatchDecision NoCandidates = new(CatalogMatchOutcome.NeedsNewName, "no-candidates", null);

	/// <summary>
	/// A nonempty purchase and 1–255 candidates with distinct IDs and display names that are distinct after lowercasing,
	/// the database's rule (IX_NormalizedDescriptions_DisplayName_Lower).
	/// An empty shortlist is not a request: the caller routes it to naming without asking Jev.
	/// </summary>
	public static bool IsValid(CatalogMatchRequest request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > MaximumPayloadBytes ||
			(request.Store is not null && (string.IsNullOrWhiteSpace(request.Store) || request.Store.Length > MaximumPayloadBytes)) ||
			request.Candidates is null || request.Candidates.Count is < 1 or > MaximumOptions)
		{
			return false;
		}

		HashSet<Guid> ids = [];
		HashSet<string> names = new(StringComparer.Ordinal);
		return request.Candidates.All(c => c is not null && c.Id != Guid.Empty && ids.Add(c.Id) &&
			!string.IsNullOrWhiteSpace(c.DisplayName) && c.DisplayName.Length <= MaximumPayloadBytes && names.Add(c.DisplayName.ToLowerInvariant()) &&
			c.ApprovedExamples is not null && c.ApprovedExamples.All(e => e is not null && !string.IsNullOrWhiteSpace(e.Description) && e.Description.Length <= MaximumPayloadBytes));
	}

	/// <summary>
	/// The stage-1 top choice: the highest Q1 probability, ties to the earlier display name in ordinal order (the order
	/// sent). The answer's own choice field is validated but does not rank.
	/// </summary>
	public static Guid TopCandidate(CatalogMatchRequest request, CatalogStage1Judgment judgment) =>
		request.Candidates.OrderByDescending(c => judgment.Probabilities[c.Id]).ThenBy(c => c.DisplayName, StringComparer.Ordinal).First().Id;

	/// <summary>Stage 2 is asked only when its answer can still change the outcome (truth-table rows 4–6).</summary>
	public static bool AsksStage2(CatalogMatchRequest request, CatalogStage1Judgment judgment, CatalogMatchThresholds thresholds) =>
		judgment.Fit >= thresholds.FitGate && judgment.Probabilities[TopCandidate(request, judgment)] >= thresholds.ChoiceFloor;

	/// <summary>
	/// The truth table. Callers validate both judgments first; stage 2 must be present exactly when
	/// <see cref="AsksStage2"/> says so, though an evaluation that always asks it may pass it anyway.
	/// </summary>
	public static CatalogMatchDecision Decide(CatalogMatchRequest request, CatalogStage1Judgment stage1, CatalogStage2Judgment? stage2,
		CatalogMatchThresholds thresholds)
	{
		ArgumentNullException.ThrowIfNull(stage1);
		ArgumentNullException.ThrowIfNull(thresholds);
		Guid top = TopCandidate(request, stage1);
		if (stage1.Fit <= thresholds.NoFitAtOrBelow) { return new(CatalogMatchOutcome.NeedsNewName, "no-fit", null); }
		if (stage1.Fit < thresholds.FitGate) { return new(CatalogMatchOutcome.Review, "uncertain-fit", null); }
		if (stage1.Probabilities[top] < thresholds.ChoiceFloor) { return new(CatalogMatchOutcome.Review, "ambiguous-choice", null); }
		if (stage2 is null) { throw new ArgumentException("Stage 2 decides this answer.", nameof(stage2)); }
		return stage2.Same >= thresholds.SameAtOrAbove ? new(CatalogMatchOutcome.MatchSuggestion, null, top)
			: stage2.Same > thresholds.DifferentAtOrBelow ? new(CatalogMatchOutcome.Review, "uncertain-variant", null)
			: new(CatalogMatchOutcome.Review, "variant-mismatch", null);
	}

	/// <summary>
	/// Exact candidate set, finite probabilities that sum to one (within 0.01), a chosen candidate holding the maximum,
	/// and a fit, confidence and usage in range.
	/// </summary>
	public static bool IsValidJudgment(CatalogMatchRequest request, CatalogStage1Judgment? judgment) =>
		judgment is { Probabilities: not null } && judgment.Model == Model && Probability(judgment.Confidence) && Probability(judgment.Fit) &&
		Tokens(judgment.InputTokens) && Tokens(judgment.OutputTokens) &&
		judgment.Probabilities.Count == request.Candidates.Count &&
		request.Candidates.All(c => judgment.Probabilities.TryGetValue(c.Id, out double p) && Probability(p)) &&
		Math.Abs(judgment.Probabilities.Values.Sum() - 1d) <= 0.01d + 1e-12d &&
		judgment.Probabilities.TryGetValue(judgment.ChosenCandidateId, out double chosen) &&
		judgment.Probabilities.Values.All(p => p <= chosen + 1e-9);

	public static bool IsValidJudgment(CatalogStage2Judgment? judgment) =>
		judgment is not null && judgment.Model == Model && Probability(judgment.Same) && Tokens(judgment.InputTokens) && Tokens(judgment.OutputTokens);

	private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

	private static bool Tokens(long value) => value is >= 0 and <= 9007199254740991;
}
