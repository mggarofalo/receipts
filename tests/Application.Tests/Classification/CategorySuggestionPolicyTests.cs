using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification;

namespace Application.Tests.Classification;

public class CategorySuggestionPolicyTests
{
	// Taxonomy order: Fruit, Dairy, Bakery (childless), Other (childless).
	private static readonly CategorySuggestionRequest Request = new("gala apples", "revision-1",
	[
		new("fruit", "Fruit", "Fresh or dried fruit", null),
		new("apples", "Apples", "Apples of any variety", "fruit"),
		new("citrus", "Citrus", "Oranges, lemons and limes", "fruit"),
		new("dairy", "Dairy", "Dairy", null),
		new("milk", "Milk", "Milk", "dairy"),
		new("bakery", "Bakery", "Bread and pastries", null),
		new("other", "Other", "Anything else", null)
	]);

	private static Dictionary<string, double> Roots(double fruit = 0.6, double dairy = 0.25, double bakery = 0.1, double other = 0.05) =>
		new() { ["fruit"] = fruit, ["dairy"] = dairy, ["bakery"] = bakery, ["other"] = other };

	private static CategoryStageJudgment Stage1(double fit = 0.9, Dictionary<string, double>? probabilities = null, string chosen = "fruit") =>
		new(CategorySuggestionPolicy.Model, chosen, 0.8, probabilities ?? Roots(), fit, 100, 10);

	// Stage-2 options for roots fruit, dairy, bakery: apples, citrus, milk, bakery.
	private static CategoryStageJudgment Stage2(string chosen, double chosenProbability) =>
		new(CategorySuggestionPolicy.Model, chosen, 0.6,
			new[] { "apples", "citrus", "milk", "bakery" }.ToDictionary(id => id,
				id => id == chosen ? chosenProbability : (1 - chosenProbability) / 3), null, 40, 4);

	private static CategorySuggestion Baseline() => CategorySuggestionPolicy.DecideStage1(Request, Stage1(), "jev").Suggestion!;

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
	public void OptionWidthIsBoundedPerStage()
	{
		static SuggestionCategory[] Flat(int count) => Enumerable.Range(0, count)
				.Select(i => new SuggestionCategory($"r{i}", $"Root {i}", "Definition", null)).ToArray();
		static IEnumerable<SuggestionCategory> Tree(string root, int children) =>
				[new(root, root, "Definition", null), .. Enumerable.Range(0, children)
								.Select(i => new SuggestionCategory($"{root}{i}", $"Child {i}", "Definition", root))];

		Assert.True(CategorySuggestionPolicy.IsValid(Request with { Categories = Flat(255) }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = Flat(256) }));
		Assert.True(CategorySuggestionPolicy.IsValid(Request with { Categories = [.. Tree("a", 255)] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [.. Tree("a", 256)] }));
		// Stage 2 offers the children of the top three roots together, so the three largest roots bound its width.
		Assert.True(CategorySuggestionPolicy.IsValid(Request with { Categories = [.. Tree("a", 85), .. Tree("b", 85), .. Tree("c", 85)] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [.. Tree("a", 86), .. Tree("b", 85), .. Tree("c", 85)] }));
	}

	[Fact]
	public void TreesDeeperThanTwoLevelsAreInvalid()
	{
		Assert.True(CategorySuggestionPolicy.IsValid(Request));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with
		{
			Categories = [.. Request.Categories, new("gala", "Gala", "Gala apples", "apples")]
		}));
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("Corner Market", true)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	public void StoreIsOptionalButNeverBlank(string? store, bool valid) =>
		Assert.Equal(valid, CategorySuggestionPolicy.IsValid(Request with { Store = store }));

	[Fact]
	public void RejectsIncompleteAndCyclicTaxonomy()
	{
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "Definition", "absent")] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "Definition", "b"), new("b", "B", "Definition", "a")] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "", null)] }));
		Assert.False(CategorySuggestionPolicy.IsValid(Request with { Categories = [new("a", "A", "Definition", null), new("a", "B", "Definition", null)] }));
	}

	[Fact]
	public void Stage2OptionsFollowTaxonomyOrderAndOfferChildlessRootsAsThemselves()
	{
		IReadOnlyList<SuggestionCategory> options = CategorySuggestionPolicy.Stage2Options(Request, ["bakery", "fruit"]);
		Assert.Equal(["apples", "citrus", "bakery"], options.Select(o => o.Id));
	}

	[Theory]
	[InlineData(0.0, "no-fit")]
	[InlineData(0.3, "no-fit")]
	[InlineData(0.5, "uncertain-fit")]
	[InlineData(0.699, "uncertain-fit")]
	public void LowFitAbstainsWithRootCandidates(double fit, string reason)
	{
		CategorySuggestionResult result = CategorySuggestionPolicy.DecideStage1(Request, Stage1(fit), "jev");

		Assert.Null(result.Failure);
		Assert.Null(result.Suggestion!.CategoryId);
		Assert.Empty(result.Suggestion.Path);
		Assert.Equal(reason, result.Suggestion.AbstentionReason);
		Assert.Equal(fit, result.Suggestion.FitProbability);
		Assert.Equal(["fruit", "dairy", "bakery"], result.Suggestion.RootCandidates!.Select(c => c.CategoryId));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, result.Suggestion));
	}

	[Theory]
	[InlineData(0.7)]
	[InlineData(1.0)]
	public void FitAtGateProposesTopRootAsBaseline(double fit)
	{
		CategorySuggestionResult result = CategorySuggestionPolicy.DecideStage1(Request, Stage1(fit), "jev");

		CategorySuggestion suggestion = result.Suggestion!;
		Assert.Null(result.Failure);
		Assert.Null(suggestion.AbstentionReason);
		Assert.Equal("fruit", suggestion.CategoryId);
		Assert.Equal(["fruit"], suggestion.Path);
		Assert.Equal(0.6, suggestion.CategoryProbability);
		Assert.Equal(0.8, suggestion.Confidence);
		Assert.Equal("local_choice", suggestion.ProbabilityScope);
		Assert.Equal(new SuggestionCandidate[] { new("fruit", 0.6), new("dairy", 0.25), new("bakery", 0.1) }, suggestion.RootCandidates!);
		Assert.Null(suggestion.LeafCandidates);
		Assert.Equal(new SuggestionProvenance("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version,
			Request.TaxonomyRevision, 100, 10), suggestion.Provenance);
		Assert.True(CategorySuggestionPolicy.IsBaseline(Request, suggestion));
	}

	[Fact]
	public void TiedRootsRankInTaxonomyOrder()
	{
		// The model chose bakery, but dairy ties it and comes first in the taxonomy.
		CategorySuggestion suggestion = CategorySuggestionPolicy.DecideStage1(Request,
			Stage1(probabilities: Roots(fruit: 0.1, dairy: 0.3, bakery: 0.3, other: 0.3), chosen: "bakery"), "jev").Suggestion!;

		Assert.Equal(["dairy", "bakery", "other"], suggestion.RootCandidates!.Select(c => c.CategoryId));
		Assert.Equal("dairy", suggestion.CategoryId);
	}

	public static TheoryData<string> InvalidStage1Judgments() =>
		["model", "sum", "chosen-not-max", "chosen-unknown", "missing-option", "extra-option", "nan-probability", "nan-fit",
			"no-fit", "fit-above-one", "negative-tokens", "confidence-above-one"];

	[Theory]
	[MemberData(nameof(InvalidStage1Judgments))]
	public void InvalidStage1JudgmentIsInvalidResponse(string defect)
	{
		CategoryStageJudgment valid = Stage1();
		CategoryStageJudgment judgment = defect switch
		{
			"model" => valid with { Model = "jev-0.0.0" },
			"sum" => valid with { Probabilities = Roots(fruit: 0.5) },
			"chosen-not-max" => valid with { ChosenCategoryId = "dairy" },
			"chosen-unknown" => valid with { ChosenCategoryId = "apples" },
			"missing-option" => valid with { Probabilities = new Dictionary<string, double> { ["fruit"] = 0.65, ["dairy"] = 0.25, ["bakery"] = 0.1 } },
			"extra-option" => valid with { Probabilities = new Dictionary<string, double>(Roots()) { ["apples"] = 0 } },
			"nan-probability" => valid with { Probabilities = Roots(other: double.NaN) },
			"nan-fit" => valid with { FitProbability = double.NaN },
			"no-fit" => valid with { FitProbability = null },
			"fit-above-one" => valid with { FitProbability = 1.01 },
			"negative-tokens" => valid with { InputTokens = -1 },
			_ => valid with { Confidence = 1.5 }
		};

		CategorySuggestionResult result = CategorySuggestionPolicy.DecideStage1(Request, judgment, "jev");

		Assert.Equal(SuggestionFailure.InvalidResponse, result.Failure);
		Assert.Null(result.Suggestion);
	}

	[Theory]
	[InlineData(0.995)]
	[InlineData(1.01)]
	public void ProbabilitiesWithinOnePercentOfOneAreAccepted(double total)
	{
		Dictionary<string, double> probabilities = Roots(fruit: 0.6 + total - 1);
		Assert.Null(CategorySuggestionPolicy.DecideStage1(Request, Stage1(probabilities: probabilities), "jev").Failure);
	}

	[Fact]
	public void Stage2LeafUnderTopRootBecomesTheProposal()
	{
		CategorySuggestion baseline = Baseline();

		CategorySuggestionResult result = CategorySuggestionPolicy.DecideStage2(Request, baseline, Stage2("citrus", 0.7));

		CategorySuggestion suggestion = result.Suggestion!;
		Assert.Null(result.Failure);
		Assert.Equal("citrus", suggestion.CategoryId);
		Assert.Equal(["fruit", "citrus"], suggestion.Path);
		Assert.Equal(0.7, suggestion.CategoryProbability);
		Assert.Equal(0.6, suggestion.Confidence);
		Assert.Equal(baseline.FitProbability, suggestion.FitProbability);
		Assert.Same(baseline.RootCandidates, suggestion.RootCandidates);
		Assert.Equal("citrus", suggestion.LeafCandidates![0].CategoryId);
		Assert.Equal(3, suggestion.LeafCandidates.Count);
		// Usage covers both stages.
		Assert.Equal(140, suggestion.Provenance.InputTokens);
		Assert.Equal(14, suggestion.Provenance.OutputTokens);
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, suggestion));
	}

	[Theory]
	[InlineData("milk")]
	[InlineData("bakery")]
	public void Stage2LeafUnderAnotherRootBacksOffToTopRoot(string leaf)
	{
		CategorySuggestion baseline = Baseline();

		CategorySuggestion suggestion = CategorySuggestionPolicy.DecideStage2(Request, baseline, Stage2(leaf, 0.7)).Suggestion!;

		Assert.Equal("fruit", suggestion.CategoryId);
		Assert.Equal(["fruit"], suggestion.Path);
		Assert.Equal(baseline.CategoryProbability, suggestion.CategoryProbability);
		Assert.Equal(baseline.Confidence, suggestion.Confidence);
		Assert.Equal(leaf, suggestion.LeafCandidates![0].CategoryId);
	}

	[Fact]
	public void Stage2LeafCandidatesTieInTaxonomyOrder()
	{
		CategoryStageJudgment judgment = new(CategorySuggestionPolicy.Model, "milk", 0.6,
			new Dictionary<string, double> { ["apples"] = 0.1, ["citrus"] = 0.3, ["milk"] = 0.3, ["bakery"] = 0.3 }, null, 1, 1);

		CategorySuggestion suggestion = CategorySuggestionPolicy.DecideStage2(Request, Baseline(), judgment).Suggestion!;

		Assert.Equal(["citrus", "milk", "bakery"], suggestion.LeafCandidates!.Select(c => c.CategoryId));
		Assert.Equal("citrus", suggestion.CategoryId);
	}

	[Fact]
	public void Stage2RejectsFitOptionMismatchAndNonBaseline()
	{
		CategorySuggestion baseline = Baseline();
		CategoryStageJudgment valid = Stage2("apples", 0.7);

		Assert.Equal(SuggestionFailure.InvalidResponse,
			CategorySuggestionPolicy.DecideStage2(Request, baseline, valid with { FitProbability = 0.9 }).Failure);
		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.DecideStage2(Request, baseline,
			valid with { Probabilities = new Dictionary<string, double>(valid.Probabilities) { ["other"] = 0 } }).Failure);
		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.DecideStage2(Request, baseline,
			valid with { Model = "jev-0.0.0" }).Failure);
		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.DecideStage2(Request,
			CategorySuggestionPolicy.DecideStage1(Request, Stage1(0.5), "jev").Suggestion!, valid).Failure);
		CategorySuggestion refined = CategorySuggestionPolicy.DecideStage2(Request, baseline, valid).Suggestion!;
		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.DecideStage2(Request, refined, valid).Failure);
	}

	[Fact]
	public void Stage2RejectsUsageThatWouldOverflowTheTotal()
	{
		CategorySuggestion baseline = Baseline();
		CategoryStageJudgment judgment = Stage2("apples", 0.7) with { InputTokens = 9007199254740991 };

		Assert.Equal(SuggestionFailure.InvalidResponse, CategorySuggestionPolicy.DecideStage2(Request, baseline, judgment).Failure);
	}

	[Fact]
	public void BaselineMustMatchItsOwnRootCandidates()
	{
		CategorySuggestion baseline = Baseline();

		Assert.True(CategorySuggestionPolicy.IsBaseline(Request, baseline));
		Assert.Equal(["fruit", "dairy", "bakery"], CategorySuggestionPolicy.Stage2RootIds(baseline));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, null));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { CategoryId = "dairy", Path = ["dairy"] }));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { CategoryProbability = 0.5 }));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { FitProbability = 0.69 }));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { RootCandidates = baseline.RootCandidates!.Take(2).ToArray() }));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with
		{
			RootCandidates = [new("fruit", 0.6), new("bakery", 0.1), new("dairy", 0.25)]
		}));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with
		{
			RootCandidates = [new("fruit", 0.6), new("apples", 0.25), new("bakery", 0.1)]
		}));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { LeafCandidates = [new("apples", 1)] }));
		Assert.False(CategorySuggestionPolicy.IsBaseline(Request, baseline with { SelectedPathSupport = 0.9 }));
	}

	[Fact]
	public void PathAndRootOfFollowParents()
	{
		Assert.Equal(["fruit", "apples"], CategorySuggestionPolicy.PathFor(Request, "apples"));
		Assert.Equal("fruit", CategorySuggestionPolicy.RootOf(Request, "apples"));
		Assert.Equal("bakery", CategorySuggestionPolicy.RootOf(Request, "bakery"));
	}
}
