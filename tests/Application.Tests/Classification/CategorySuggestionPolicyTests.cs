using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification;

namespace Application.Tests.Classification;

public class CategorySuggestionPolicyTests
{
	[Fact]
	public void StoredProbabilityScopeDefaultsForLegacyAndPreservesLocalChoice()
	{
		var suggestion = new CategorySuggestion("leaf", ["root", "leaf"], null, 0.9, 0.8, 0.7,
				new SuggestionProvenance("jev", CategorySuggestionPolicy.Model,
						CategorySuggestionPolicy.Version, "revision", 1, 1), "local_choice");
		var stored = JsonSerializer.SerializeToNode(suggestion)!.AsObject();
		Assert.Equal("local_choice", JsonSerializer.Deserialize<CategorySuggestion>(stored.ToJsonString())!.ProbabilityScope);
		stored.Remove("ProbabilityScope");
		Assert.Equal("global_choice", JsonSerializer.Deserialize<CategorySuggestion>(stored.ToJsonString())!.ProbabilityScope);
	}
	[Fact]
	public void ChoiceWidthCountsParentSelfOption()
	{
		static SuggestionCategory[] Roots(int count) => Enumerable.Range(0, count)
				.Select(i => new SuggestionCategory($"r{i}", $"Root {i}", "Definition", null)).ToArray();
		static SuggestionCategory[] Children(int count) =>
				[new("root", "Root", "Definition", null), .. Enumerable.Range(0, count)
								.Select(i => new SuggestionCategory($"c{i}", $"Child {i}", "Definition", "root"))];

		Assert.True(CategorySuggestionPolicy.IsValid(Request with { Categories = Roots(255) }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = Roots(256) }));
		Assert.True(CategorySuggestionPolicy.IsValid(Request with { Categories = Children(254) }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = Children(255) }));
	}

	[Theory]
	[InlineData(0.3, "no-fit")]
	[InlineData(0.5, "uncertain-fit")]
	[InlineData(0.7, null)]
	public void HierarchyValidatesAllBranchesBeforeFitAndTraversesOnlySelectedPath(double fit, string? reason)
	{
		CategorySuggestionRequest request = Request with
		{
			Categories =
				[new("r", "Root", "Broad", null), new("c", "Child", "Specific", "r"),
						 new("g", "Grandchild", "Specific", "c"), new("other", "Other", "Broad", null),
						 new("unused", "Unused", "Specific", "other")]
		};
		static CategoryChoiceJudgment Choice(string selected, params string[] ids) =>
				new(selected, 0.8, ids.ToDictionary(id => id, id => id == selected ? 1.0 : 0.0));
		Dictionary<string, CategoryChoiceJudgment> choices = new()
		{
			["category"] = Choice("r", "r", "other"),
			["branch_0"] = Choice("c", "r", "c"),
			["branch_1"] = Choice("g", "c", "g"),
			["branch_2"] = Choice("unused", "other", "unused")
		};
		CategoryHierarchyJudgment judgment = new(CategorySuggestionPolicy.Model, request.TaxonomyRevision,
				fit, choices, 100, 10);
		CategorySuggestionResult result = CategorySuggestionPolicy.Decide(request, judgment, "test");
		Assert.Null(result.Failure);
		Assert.Equal(reason, result.Suggestion!.AbstentionReason);
		Assert.Equal(reason is null ? "g" : null, result.Suggestion.CategoryId);
		Assert.Equal(reason is null ? new[] { "r", "c", "g" } : [], result.Suggestion.Path);
		Assert.Equal("local_choice", result.Suggestion.ProbabilityScope);

		choices.Remove("branch_2");
		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.Decide(request, judgment, "test").Failure);
	}
	[Fact]
	public void HierarchyRejectsNullSelectedChoiceAsInvalidResponse()
	{
		var choices = new Dictionary<string, CategoryChoiceJudgment>
		{
			["category"] = new(null!, 0.8, new Dictionary<string, double> { ["parent"] = 1 }),
			["branch_0"] = new("parent", 0.8, new Dictionary<string, double> { ["parent"] = 1, ["child"] = 0 })
		};
		var judgment = new CategoryHierarchyJudgment(CategorySuggestionPolicy.Model,
				Request.TaxonomyRevision, 0.9, choices, 100, 10);
		Assert.Equal(SuggestionFailure.InvalidResponse,
				CategorySuggestionPolicy.Decide(Request, judgment, "test").Failure);
	}
	private static readonly CategorySuggestionRequest Request = new("broad purchase", "revision-1", [new("parent", "Parent", "All purchases including children", null), new("child", "Child", "A specific purchase", "parent")]);
	private static CategoryJudgment Judgment(double fit = 0.9) => new(CategorySuggestionPolicy.Model, Request.TaxonomyRevision, "parent", fit, 0.01, new Dictionary<string, double> { ["parent"] = 0.8, ["child"] = 0.2 }, 100, 10);

	[Theory]
	[InlineData(0, "no-fit")]
	[InlineData(0.3, "no-fit")]
	[InlineData(0.31, "uncertain-fit")]
	[InlineData(0.699, "uncertain-fit")]
	public void ValidNoFitAbstainsWithoutError(double fit, string reason)
	{
		CategorySuggestionResult actual = CategorySuggestionPolicy.Decide(Request, Judgment(fit), "test");
		Assert.Null(actual.Failure);
		Assert.Null(actual.Suggestion!.CategoryId);
		Assert.Equal(reason, actual.Suggestion.AbstentionReason);
	}

	[Theory]
	[InlineData(0.7)]
	[InlineData(1)]
	public void BroadParentIsSuggestedWithoutConfidenceGate(double fit)
	{
		CategorySuggestionResult actual = CategorySuggestionPolicy.Decide(Request, Judgment(fit), "test");
		Assert.Equal("parent", actual.Suggestion!.CategoryId);
		Assert.Equal(new[] { "parent" }, actual.Suggestion.Path);
		Assert.Equal(Request.TaxonomyRevision, actual.Suggestion.Provenance.TaxonomyRevision);
		Assert.Equal(CategorySuggestionPolicy.Version, actual.Suggestion.Provenance.QuestionPolicy);
	}

	[Fact]
	public void InvalidIdentityRevisionModelUsageOrDistributionIsFailureEvenForNoFit()
	{
		CategoryJudgment valid = Judgment(0);
		CategoryJudgment[] invalid = [valid with { CategoryId = "unknown" }, valid with { TaxonomyRevision = "stale" }, valid with { Model = "other" }, valid with { InputTokens = -1 }, valid with { FitProbability = double.NaN }, valid with { Probabilities = new Dictionary<string, double> { ["parent"] = 0.1, ["child"] = 0.9 } }, valid with { Probabilities = new Dictionary<string, double> { ["parent"] = 0.8, ["unknown"] = 0.2 } }];
		foreach (CategoryJudgment judgment in invalid)
		{
			CategorySuggestionResult actual = CategorySuggestionPolicy.Decide(Request, judgment, "test");
			Assert.Equal(SuggestionFailure.InvalidResponse, actual.Failure);
			Assert.Null(actual.Suggestion);
		}
	}

	[Fact]
	public void RejectsIncompleteAndCyclicTaxonomy()
	{
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "Definition", "absent")] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "Definition", "b"), new("b", "B", "Definition", "a")] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "", null)] }));
	}
}
