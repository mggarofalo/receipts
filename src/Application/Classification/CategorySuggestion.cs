namespace Application.Classification;

public sealed record SuggestionCategory(string Id, string Name, string Definition, string? ParentId);

/// <summary>What a person sees on the receipt line: the item text and, when known, the store. Nothing else is sent.</summary>
public sealed record CategorySuggestionRequest(string Description, string TaxonomyRevision, IReadOnlyList<SuggestionCategory> Categories,
	string? Store = null);

/// <summary>One validated Choice answer keyed by category ID, plus the stage-1 fit Noul when it was asked.</summary>
public sealed record CategoryStageJudgment(string Model, string ChosenCategoryId, double Confidence,
	IReadOnlyDictionary<string, double> Probabilities, double? FitProbability, long InputTokens, long OutputTokens);

public sealed record SuggestionProvenance(string Provider, string Model, string QuestionPolicy, string TaxonomyRevision, long InputTokens, long OutputTokens);

/// <summary>A candidate category and the probability its own stage's Choice gave it.</summary>
public sealed record SuggestionCandidate(string CategoryId, double Probability);

/// <summary>
/// RootCandidates are the stage-1 top roots in rank order (they select the stage-2 options); LeafCandidates are the
/// stage-2 top options in rank order and are absent until stage 2 has answered. SelectedPathSupport is only read
/// back from results stored by an earlier policy.
/// </summary>
public sealed record CategorySuggestion(string? CategoryId, IReadOnlyList<string> Path, string? AbstentionReason,
		double FitProbability, double CategoryProbability, double Confidence, SuggestionProvenance Provenance,
		string ProbabilityScope = "global_choice", double? SelectedPathSupport = null,
		IReadOnlyList<SuggestionCandidate>? RootCandidates = null, IReadOnlyList<SuggestionCandidate>? LeafCandidates = null);
public enum SuggestionFailure { Disabled, MissingCredential, InvalidInput, PayloadTooLarge, Busy, Authentication, RateLimited, Timeout, Unavailable, InvalidResponse }
/// <summary>Billed tokens one provider response reported.</summary>
public sealed record SuggestionUsage(long InputTokens, long OutputTokens)
{
	public bool IsValid => InputTokens is >= 0 and <= 9007199254740991 && OutputTokens is >= 0 and <= 9007199254740991;
}

/// <summary>Usage is this stage's own billed usage whenever a response reported it, including on a failure.</summary>
public sealed record CategorySuggestionResult(CategorySuggestion? Suggestion, SuggestionFailure? Failure, SuggestionUsage? Usage = null)
{
	public static CategorySuggestionResult Failed(SuggestionFailure failure, SuggestionUsage? usage = null) => new(null, failure, usage);
}

/// <summary>
/// Two-stage hierarchy policy. Stage 1 chooses among the roots and asks whether any root fits. Stage 2 chooses among
/// the children of the stage-1 top roots. Back-off to the root is decided here, never offered as an option.
/// </summary>
public static class CategorySuggestionPolicy
{
	public const string Version = "classification-questions-v7";
	public const string Model = "jev-1.14.0";
	public const int MaximumPayloadBytes = 65536;
	public const int TopRoots = 3;
	public const int TopLeaves = 3;
	public const int MaximumOptions = 255;
	public const double NoFitAtOrBelow = 0.3;
	public const double FitGate = 0.7;
	private const long MaximumTokens = 9007199254740991;

	/// <summary>A valid request names a nonempty purchase and a two-level tree: roots and their immediate children.</summary>
	public static bool IsValid(CategorySuggestionRequest request)
	{
		if (request is null || request.Categories is null || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > MaximumPayloadBytes ||
			string.IsNullOrWhiteSpace(request.TaxonomyRevision) || request.TaxonomyRevision.Length > 200 ||
			(request.Store is not null && (string.IsNullOrWhiteSpace(request.Store) || request.Store.Length > MaximumPayloadBytes)) ||
			request.Categories.Count < 1)
		{
			return false;
		}

		Dictionary<string, SuggestionCategory> categories = new(StringComparer.Ordinal);
		foreach (SuggestionCategory category in request.Categories)
		{
			if (category is null || string.IsNullOrWhiteSpace(category.Id) || category.Id.Length > 200 || string.IsNullOrWhiteSpace(category.Name) ||
				string.IsNullOrWhiteSpace(category.Definition) || category.Name.Length > MaximumPayloadBytes ||
				category.Definition.Length > MaximumPayloadBytes || !categories.TryAdd(category.Id, category))
			{
				return false;
			}
		}

		// Two stages reach depth two. A deeper tree fails explicitly rather than silently losing its leaves.
		if (categories.Values.Any(c => c.ParentId is not null &&
			(!categories.TryGetValue(c.ParentId, out SuggestionCategory? parent) || parent.ParentId is not null)))
		{
			return false;
		}

		int roots = categories.Values.Count(category => category.ParentId is null);
		int largestStage2 = Roots(request).Select(r => Math.Max(1, Children(request, r.Id).Count))
			.OrderDescending().Take(TopRoots).Sum();
		return roots is >= 1 and <= MaximumOptions && largestStage2 <= MaximumOptions;
	}

	/// <summary>Stage-1 options: every root, in taxonomy order.</summary>
	public static IReadOnlyList<SuggestionCategory> Roots(CategorySuggestionRequest request) =>
		[.. request.Categories.Where(c => c.ParentId is null)];

	/// <summary>
	/// Stage-2 options: the children of the given roots in taxonomy order (not stage-1 rank, so the stage-1 winner
	/// gets no first-option advantage). A root without children is offered as itself.
	/// </summary>
	public static IReadOnlyList<SuggestionCategory> Stage2Options(CategorySuggestionRequest request, IReadOnlyCollection<string> rootIds)
	{
		List<SuggestionCategory> options = [];
		foreach (SuggestionCategory root in Roots(request).Where(r => rootIds.Contains(r.Id)))
		{
			IReadOnlyList<SuggestionCategory> children = Children(request, root.Id);
			if (children.Count == 0) { options.Add(root); }
			else { options.AddRange(children); }
		}
		return options;
	}

	/// <summary>The roots whose children stage 2 offers: the stage-1 baseline's root candidates.</summary>
	public static IReadOnlyList<string> Stage2RootIds(CategorySuggestion baseline) =>
		[.. (baseline.RootCandidates ?? []).Select(c => c.CategoryId)];

	/// <summary>
	/// Validates stage 1. Fit at or below 0.3 abstains as no-fit and below 0.7 as uncertain-fit; otherwise the result
	/// proposes the top root and is the baseline for stage 2.
	/// </summary>
	public static CategorySuggestionResult DecideStage1(CategorySuggestionRequest request, CategoryStageJudgment judgment, string provider)
	{
		IReadOnlyList<SuggestionCategory> roots = IsValid(request) ? Roots(request) : [];
		if (roots.Count == 0 || string.IsNullOrWhiteSpace(provider) || !IsValidJudgment(judgment, roots) ||
			judgment.FitProbability is not double fit || !Probability(fit))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		List<SuggestionCandidate> top = Rank(roots, judgment.Probabilities, TopRoots);
		string? reason = fit <= NoFitAtOrBelow ? "no-fit" : fit < FitGate ? "uncertain-fit" : null;
		SuggestionCandidate chosen = top[0];
		return new(new(reason is null ? chosen.CategoryId : null, reason is null ? PathFor(request, chosen.CategoryId) : [], reason,
			fit, chosen.Probability, judgment.Confidence,
			new(provider, judgment.Model, Version, request.TaxonomyRevision, judgment.InputTokens, judgment.OutputTokens),
			"local_choice", RootCandidates: top), null);
	}

	/// <summary>
	/// Validates stage 2 against the baseline's roots. The proposal is the top child when it lies under the stage-1 top
	/// root, otherwise the stage-1 top root (back-off). Probability and confidence describe the proposal's own stage.
	/// </summary>
	public static CategorySuggestionResult DecideStage2(CategorySuggestionRequest request, CategorySuggestion baseline, CategoryStageJudgment judgment)
	{
		if (!IsValid(request) || !IsBaseline(request, baseline))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		IReadOnlyList<SuggestionCategory> options = Stage2Options(request, Stage2RootIds(baseline));
		if (!IsValidJudgment(judgment, options) || judgment.FitProbability is not null ||
			judgment.InputTokens > MaximumTokens - baseline.Provenance.InputTokens ||
			judgment.OutputTokens > MaximumTokens - baseline.Provenance.OutputTokens)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		List<SuggestionCandidate> leaves = Rank(options, judgment.Probabilities, TopLeaves);
		string topRoot = baseline.RootCandidates![0].CategoryId;
		bool consistent = RootOf(request, leaves[0].CategoryId) == topRoot;
		string chosen = consistent ? leaves[0].CategoryId : topRoot;
		return new(baseline with
		{
			CategoryId = chosen,
			Path = PathFor(request, chosen),
			CategoryProbability = consistent ? leaves[0].Probability : baseline.CategoryProbability,
			Confidence = consistent ? judgment.Confidence : baseline.Confidence,
			Provenance = baseline.Provenance with
			{
				InputTokens = baseline.Provenance.InputTokens + judgment.InputTokens,
				OutputTokens = baseline.Provenance.OutputTokens + judgment.OutputTokens
			},
			LeafCandidates = leaves
		}, null);
	}

	/// <summary>A stage-1 result that may start stage 2: it proposes its own top root and has no stage-2 answer.</summary>
	public static bool IsBaseline(CategorySuggestionRequest request, CategorySuggestion? baseline) =>
		baseline is { AbstentionReason: null, CategoryId: not null, LeafCandidates: null, SelectedPathSupport: null, RootCandidates: { Count: > 0 } roots } &&
		baseline.Path is not null && baseline.Provenance is not null &&
		Probability(baseline.FitProbability) && baseline.FitProbability >= FitGate &&
		roots.Count == Math.Min(TopRoots, Roots(request).Count) &&
		roots.All(c => c is not null && Probability(c.Probability) && request.Categories.Any(x => x.Id == c.CategoryId && x.ParentId is null)) &&
		roots.Select(c => c.CategoryId).Distinct(StringComparer.Ordinal).Count() == roots.Count &&
		roots.Zip(roots.Skip(1)).All(pair => pair.First.Probability >= pair.Second.Probability) &&
		baseline.CategoryId == roots[0].CategoryId && baseline.CategoryProbability == roots[0].Probability &&
		baseline.Path.SequenceEqual(PathFor(request, baseline.CategoryId));

	public static string RootOf(CategorySuggestionRequest request, string id) => PathFor(request, id)[0];

	public static IReadOnlyList<string> PathFor(CategorySuggestionRequest request, string id)
	{
		List<string> path = [];
		string? current = id;
		while (current is not null)
		{
			path.Insert(0, current);
			current = request.Categories.Single(category => category.Id == current).ParentId;
		}
		return path;
	}

	private static IReadOnlyList<SuggestionCategory> Children(CategorySuggestionRequest request, string parentId) =>
		[.. request.Categories.Where(c => c.ParentId == parentId)];

	/// <summary>Exact option set, finite probabilities that sum to one, and a chosen option that holds the maximum.</summary>
	public static bool IsValidJudgment(CategoryStageJudgment? judgment, IReadOnlyList<SuggestionCategory> options) =>
		judgment is { Probabilities: not null } && judgment.Model == Model && !string.IsNullOrWhiteSpace(judgment.ChosenCategoryId) &&
		Probability(judgment.Confidence) && judgment.InputTokens is >= 0 and <= MaximumTokens && judgment.OutputTokens is >= 0 and <= MaximumTokens &&
		judgment.Probabilities.Count == options.Count &&
		options.All(o => judgment.Probabilities.TryGetValue(o.Id, out double p) && Probability(p)) &&
		DistributionTotalValid(judgment.Probabilities.Values) &&
		judgment.Probabilities.TryGetValue(judgment.ChosenCategoryId, out double selected) &&
		judgment.Probabilities.Values.All(value => value <= selected + 1e-9);

	// Ties keep taxonomy order.
	private static List<SuggestionCandidate> Rank(IReadOnlyList<SuggestionCategory> options, IReadOnlyDictionary<string, double> probabilities, int take) =>
		[.. options.Select((o, i) => (o.Id, i)).OrderByDescending(x => probabilities[x.Id]).ThenBy(x => x.i)
			.Take(take).Select(x => new SuggestionCandidate(x.Id, probabilities[x.Id]))];

	// Accommodate accumulation rounding at the inclusive one-percent boundary;
	// this allowance is far below the provider tolerance.
	private static bool DistributionTotalValid(IEnumerable<double> probabilities) =>
		Math.Abs(probabilities.Sum() - 1d) <= 0.01d + 1e-12d;

	private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
