using Application.Classification;

namespace Application.Tests.Classification;

public class CategorySuggestionPolicyTests
{
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
