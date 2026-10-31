using System.Text.Json.Serialization;

namespace Application.Classification;

/// <summary>
/// Configuration section <c>CategoryAssistance</c>. Leaf writes stay off until leaf accuracy is validated on the
/// restructured item-type tree; the root threshold comes from the round-4 operating point.
/// </summary>
public sealed class CategoryAutoWriteOptions
{
	public double RootAutoWriteThreshold { get; set; } = 0.90;
	public double LeafAutoWriteThreshold { get; set; } = 0.90;
	public bool LeafAutoWriteEnabled { get; set; }

	public bool IsValid() => Gate(RootAutoWriteThreshold) && Gate(LeafAutoWriteThreshold);

	// Below the suggestion gate an automatic write would be less certain than a suggestion.
	private static bool Gate(double value) => double.IsFinite(value) && value is >= CategorySuggestionPolicy.FitGate and <= 1;
}

// Persisted in jev evidence; a name reads better than an ordinal and survives reordering.
[JsonConverter(typeof(JsonStringEnumConverter<CategoryAutoWriteLevel>))]
public enum CategoryAutoWriteLevel { Root, Leaf }

/// <summary>The category to write and the evidence that justified it.</summary>
public sealed record CategoryAutoWriteDecision(string CategoryId, CategoryAutoWriteLevel Level, double FitProbability,
	double RootProbability, double? LeafProbability);

/// <summary>Pure decision over a completed two-stage suggestion. It never writes; callers apply it under their own locks.</summary>
public static class CategoryAutoWritePolicy
{
	/// <summary>
	/// Strong root: fit at or above the suggestion gate and the stage-1 top root at or above the root threshold. Then the
	/// stage-2 top option replaces it only when leaf writes are enabled, it reaches the leaf threshold and lies under
	/// that same root. Anything weaker stays a suggestion. Returns null for abstentions and incomplete results.
	/// </summary>
	public static CategoryAutoWriteDecision? Decide(CategorySuggestionRequest request, CategorySuggestion? suggestion, CategoryAutoWriteOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (!options.IsValid() || !CategorySuggestionPolicy.IsValid(request) ||
			suggestion is not { CategoryId: not null, AbstentionReason: null, RootCandidates: { Count: > 0 } roots, LeafCandidates: { Count: > 0 } leaves } ||
			suggestion.Provenance?.QuestionPolicy != CategorySuggestionPolicy.Version ||
			suggestion.FitProbability < CategorySuggestionPolicy.FitGate || roots[0].Probability < options.RootAutoWriteThreshold ||
			!request.Categories.Any(c => c.Id == roots[0].CategoryId && c.ParentId is null) ||
			!request.Categories.Any(c => c.Id == leaves[0].CategoryId))
		{
			return null;
		}

		string root = roots[0].CategoryId;
		bool leaf = options.LeafAutoWriteEnabled && leaves[0].Probability >= options.LeafAutoWriteThreshold &&
			CategorySuggestionPolicy.RootOf(request, leaves[0].CategoryId) == root;
		return leaf
			? new(leaves[0].CategoryId, CategoryAutoWriteLevel.Leaf, suggestion.FitProbability, roots[0].Probability, leaves[0].Probability)
			: new(root, CategoryAutoWriteLevel.Root, suggestion.FitProbability, roots[0].Probability, leaves[0].Probability);
	}
}

/// <summary>Reason JSON of a <c>jev</c> assignment: the result, policy and thresholds that justified the automatic write.</summary>
public sealed record JevAutoWriteEvidence(Guid AttemptId, Guid RequestId, string QuestionPolicy, string Model, string TaxonomyRevision,
	CategoryAutoWriteLevel Level, double FitProbability, double RootProbability, double? LeafProbability,
	double RootThreshold, double LeafThreshold, bool LeafEnabled);
