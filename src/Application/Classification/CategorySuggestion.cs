namespace Application.Classification;

public sealed record SuggestionCategory(string Id, string Name, string Definition, string? ParentId);
public sealed record CategorySuggestionRequest(string Description, string TaxonomyRevision, IReadOnlyList<SuggestionCategory> Categories);
public sealed record CategoryJudgment(string Model, string TaxonomyRevision, string CategoryId, double FitProbability,
	double Confidence, IReadOnlyDictionary<string, double> Probabilities, long InputTokens, long OutputTokens);
public sealed record CategoryChoiceJudgment(string CategoryId, double Confidence, IReadOnlyDictionary<string, double> Probabilities);
public sealed record CategoryHierarchyJudgment(string Model, string TaxonomyRevision, double FitProbability,
		IReadOnlyDictionary<string, CategoryChoiceJudgment> Choices, long InputTokens, long OutputTokens);
public sealed record SuggestionProvenance(string Provider, string Model, string QuestionPolicy, string TaxonomyRevision, long InputTokens, long OutputTokens);
public sealed record CategorySuggestion(string? CategoryId, IReadOnlyList<string> Path, string? AbstentionReason,
		double FitProbability, double CategoryProbability, double Confidence, SuggestionProvenance Provenance,
		string ProbabilityScope = "global_choice");
public enum SuggestionFailure { Disabled, MissingCredential, InvalidInput, PayloadTooLarge, Busy, Authentication, RateLimited, Timeout, Unavailable, InvalidResponse }
public sealed record CategorySuggestionResult(CategorySuggestion? Suggestion, SuggestionFailure? Failure)
{
	public static CategorySuggestionResult Failed(SuggestionFailure failure) => new(null, failure);
}

/// <summary>Suggestions require explicit user confirmation. This contract never assigns a category.</summary>
public interface ICategorySuggestionProvider
{
	bool IsAvailable { get; }
	Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken);
}

public static class CategorySuggestionPolicy
{
	public const string Version = "classification-questions-v5";
	public const string Model = "jev-1.14.0";
	public const int MaximumPayloadBytes = 65536;

	public static bool IsValid(CategorySuggestionRequest request)
	{
		if (request is null || request.Categories is null || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > MaximumPayloadBytes ||
			string.IsNullOrWhiteSpace(request.TaxonomyRevision) || request.TaxonomyRevision.Length > 200 ||
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
		foreach (SuggestionCategory category in categories.Values)
		{
			HashSet<string> seen = [category.Id];
			string? parent = category.ParentId;
			while (parent is not null)
			{
				if (!seen.Add(parent) || !categories.TryGetValue(parent, out SuggestionCategory? ancestor))
				{
					return false;
				}

				parent = ancestor.ParentId;
			}
		}
		if (categories.Values.Count(category => category.ParentId is null) is < 1 or > 255 ||
			categories.Values.GroupBy(category => category.ParentId).Any(group => group.Key is not null && group.Count() > 254))
		{
			return false;
		}
		return true;
	}

	public static CategorySuggestionResult Decide(CategorySuggestionRequest request, CategoryHierarchyJudgment judgment, string provider)
	{
		if (!IsValid(request) || judgment is null || judgment.Choices is null || judgment.Model != Model ||
			judgment.TaxonomyRevision != request.TaxonomyRevision || !Probability(judgment.FitProbability) ||
			judgment.InputTokens is < 0 or > 9007199254740991 || judgment.OutputTokens is < 0 or > 9007199254740991)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		var children = request.Categories.GroupBy(x => x.ParentId).ToDictionary(x => x.Key ?? "", x => x.ToArray());
		var internals = request.Categories.Where(x => children.ContainsKey(x.Id)).ToArray();
		var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			["category"] = children[""].Select(x => x.Id).ToArray()
		};
		for (int i = 0; i < internals.Length; i++)
		{
			expected[$"branch_{i}"] = [internals[i].Id, .. children[internals[i].Id].Select(x => x.Id)];
		}

		if (judgment.Choices.Count != expected.Count)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		foreach (var (key, ids) in expected)
		{
			if (!judgment.Choices.TryGetValue(key, out CategoryChoiceJudgment? choice) || choice is null ||
				choice.Probabilities is null || string.IsNullOrWhiteSpace(choice.CategoryId) ||
				!Probability(choice.Confidence) || choice.Probabilities.Count != ids.Length ||
				!choice.Probabilities.TryGetValue(choice.CategoryId, out double selected) ||
				ids.Any(id => !choice.Probabilities.TryGetValue(id, out double p) || !Probability(p)) ||
				Math.Abs(choice.Probabilities.Values.Sum() - 1) > 0.01 ||
				choice.Probabilities.Values.Any(value => value > selected + 1e-9))
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
			}
		}

		string? reason = judgment.FitProbability <= 0.3 ? "no-fit" : judgment.FitProbability < 0.7 ? "uncertain-fit" : null;
		string id = judgment.Choices["category"].CategoryId;
		CategoryChoiceJudgment terminal = judgment.Choices["category"];
		if (reason is null)
		{
			while (children.ContainsKey(id))
			{
				int index = Array.FindIndex(internals, x => x.Id == id);
				terminal = judgment.Choices[$"branch_{index}"];
				if (terminal.CategoryId == id)
				{
					break;
				}

				id = terminal.CategoryId;
			}
		}
		return new(new(reason is null ? id : null, reason is null ? PathFor(request, id) : [], reason,
			judgment.FitProbability, terminal.Probabilities[terminal.CategoryId], terminal.Confidence,
			new(provider, judgment.Model, Version, request.TaxonomyRevision, judgment.InputTokens, judgment.OutputTokens),
			"local_choice"), null);
	}

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

	public static CategorySuggestionResult Decide(CategorySuggestionRequest request, CategoryJudgment judgment, string provider)
	{
		if (!IsValid(request) || judgment is null || judgment.Probabilities is null || string.IsNullOrWhiteSpace(judgment.CategoryId) || judgment.Model != Model || judgment.TaxonomyRevision != request.TaxonomyRevision ||
			!Probability(judgment.FitProbability) || !Probability(judgment.Confidence) || judgment.InputTokens is < 0 or > 9007199254740991 || judgment.OutputTokens is < 0 or > 9007199254740991 ||
			judgment.Probabilities.Count != request.Categories.Count || !judgment.Probabilities.TryGetValue(judgment.CategoryId, out double selected) ||
			request.Categories.Any(category => !judgment.Probabilities.TryGetValue(category.Id, out double probability) || !Probability(probability)) ||
			Math.Abs(judgment.Probabilities.Values.Sum() - 1) > 0.01 || judgment.Probabilities.Values.Any(value => value > selected + 1e-9))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
		}

		string? reason = judgment.FitProbability <= 0.3 ? "no-fit" : judgment.FitProbability < 0.7 ? "uncertain-fit" : null;
		return new(new(reason is null ? judgment.CategoryId : null, reason is null ? PathFor(request, judgment.CategoryId) : [], reason,
			judgment.FitProbability, selected, judgment.Confidence,
			new(provider, judgment.Model, Version, request.TaxonomyRevision, judgment.InputTokens, judgment.OutputTokens)), null);
	}

	private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
