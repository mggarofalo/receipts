using Application.Classification;
using Application.Classification.Experimental;

namespace Application.Tests.Classification;

public class SupportedCategoryCandidateTests
{
	private static readonly SuggestionCategory[] Categories =
	[
		new("food", "Food", "Food purchased for consumption", null),
		new("fruit", "Fruit", "Fresh fruit", "food"),
		new("apple", "Apples", "Fresh apples", "fruit"),
		new("pear", "Pears", "Fresh pears", "fruit"),
		new("tools", "Tools", "Hand and power tools", null)
	];

	[Fact]
	public void SelectsDeepestSupportedPathWithoutInventingAPathProbability()
	{
		CandidateDecision actual = Decide(Scores(("food", .95, .95), ("fruit", .95, .95), ("apple", .95, .95)));

		Assert.Equal("apple", actual.CategoryId);
		Assert.Equal(new[] { "food", "fruit", "apple" }, actual.Path);
		Assert.Null(actual.AbstentionReason);
		Assert.Null(actual.Failure);
	}

	[Theory]
	[InlineData(.5, .9)]
	[InlineData(.95, .4)]
	public void UnsettledSiblingFallsBackToExplicitlySupportedCommonParent(double support, double sufficientContext)
	{
		CandidateDecision actual = Decide(Scores(("food", .95, .95), ("fruit", .95, .95),
			("apple", .95, .95), ("pear", support, sufficientContext)));

		Assert.Equal("fruit", actual.CategoryId);
	}

	[Fact]
	public void ConflictingRootsAbstain()
	{
		CandidateDecision actual = Decide(Scores(("food", .95, .95), ("apple", .95, .95), ("tools", .5, .5)));

		Assert.Null(actual.CategoryId);
		Assert.Equal("conflicting-paths", actual.AbstentionReason);
	}

	[Fact]
	public void DoesNotInferCommonParentSupportFromSupportedChildren()
	{
		CandidateDecision actual = Decide(Scores(("food", .9, .9), ("fruit", .5, .5),
			("apple", .9, .9), ("pear", .9, .9)));

		Assert.Equal("food", actual.CategoryId);
	}

	[Theory]
	[InlineData(.2, 0, "no-purchase-evidence")]
	[InlineData(.5, 0, "uncertain-purchase-evidence")]
	[InlineData(.9, .5, "uncovered-alternatives")]
	public void GlobalEvidenceAndCoverageAreSeparateGates(double evidence, double alternatives, string reason)
	{
		CandidateDecision actual = Decide(Scores(("food", .9, .9)), evidence, alternatives);

		Assert.Null(actual.CategoryId);
		Assert.Equal(reason, actual.AbstentionReason);
		Assert.Null(actual.Failure);
	}

	[Fact]
	public void ParentOnlyEvidenceCanSelectBroadCategory()
	{
		CandidateDecision actual = Decide(Scores(("food", .9, .9), ("fruit", .5, .3)));

		Assert.Equal("food", actual.CategoryId);
	}

	[Theory]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	[InlineData(-.01)]
	[InlineData(1.01)]
	public void InvalidEvidenceIsFailureEvenWhenPurchaseEvidenceWouldAbstain(double invalid)
	{
		Dictionary<string, CandidatePathJudgment> scores = Scores(("food", invalid, .9));
		CandidateDecision actual = Decide(scores, .1);

		Assert.Equal("invalid-judgment", actual.Failure);
		Assert.Null(actual.AbstentionReason);
	}

	[Fact]
	public void MissingExtraAndWrongRevisionAnswersAreFailures()
	{
		CandidateVerificationPlan plan = Plan();
		Dictionary<string, CandidatePathJudgment> scores = Scores(("food", .9, .9));
		scores.Remove("tools");
		Assert.Equal("invalid-judgment", SupportedCategoryCandidate.Decide(plan, new("rev", .9, .1, scores)).Failure);
		scores["invented"] = new(.9, .9);
		Assert.Equal("invalid-judgment", SupportedCategoryCandidate.Decide(plan, new("rev", .9, .1, scores)).Failure);
		Assert.Equal("invalid-judgment", SupportedCategoryCandidate.Decide(plan, new("stale", .9, .1, Scores())).Failure);
	}

	[Fact]
	public void PlanPreservesDescriptionStoreCodeAndFullPathDefinitions()
	{
		CandidateVerificationPlan actual = Plan();

		Assert.Equal("APPLES", actual.Purchase.Description);
		Assert.Equal("Neighborhood Market", actual.Purchase.Store);
		Assert.Equal("4017", actual.Purchase.ProductCode);
		Assert.Equal(new[] { "Food", "Fruit", "Apples" }, actual.Paths.Single(x => x.CategoryId == "apple").Categories.Select(x => x.Name));
		Assert.All(actual.Paths.SelectMany(x => x.Categories), category => Assert.False(string.IsNullOrWhiteSpace(category.Definition)));
	}

	[Fact]
	public void PlanBoundsShortlistAndExpandsAncestorsWithoutAcceptingInventedIds()
	{
		CandidateVerificationPlan actual = SupportedCategoryCandidate.Plan(new("APPLES", null, null), "rev", Categories, ["apple", "pear"]);

		Assert.Equal(new[] { "apple", "food", "fruit", "pear" }, actual.Paths.Select(x => x.CategoryId).Order());
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("APPLES", null, null), "rev", Categories, ["invented"]));
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("APPLES", null, null), "rev", Categories,
			Enumerable.Repeat("apple", SupportedCategoryCandidate.MaximumShortlistSize + 1).ToArray()));
	}

	[Fact]
	public void JudgmentMustIncludeExplicitExpandedAncestorSupport()
	{
		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(new("APPLES", null, null), "rev", Categories, ["apple"]);
		CandidateDecision actual = SupportedCategoryCandidate.Decide(plan, new("rev", .9, .1,
			new Dictionary<string, CandidatePathJudgment> { ["apple"] = new(.95, .95) }));

		Assert.Equal("invalid-judgment", actual.Failure);
	}

	private static CandidateVerificationPlan Plan() => SupportedCategoryCandidate.Plan(
		new("APPLES", "Neighborhood Market", "4017"), "rev", Categories, Categories.Select(x => x.Id).ToArray());

	private static CandidateDecision Decide(Dictionary<string, CandidatePathJudgment> scores, double evidence = .95, double alternatives = .1)
		=> SupportedCategoryCandidate.Decide(Plan(), new("rev", evidence, alternatives, scores));

	private static Dictionary<string, CandidatePathJudgment> Scores(params (string Id, double Support, double Context)[] supported)
	{
		Dictionary<string, CandidatePathJudgment> scores = Categories.ToDictionary(x => x.Id, _ => new CandidatePathJudgment(.1, .1));
		foreach ((string id, double support, double context) in supported)
		{
			scores[id] = new(support, context);
		}
		return scores;
	}

	[Fact]
	public void StrongCategoryWithoutSufficientContextAbstains()
	{
		CandidateDecision actual = Decide(Scores(("food", .95, .5)));
		Assert.Equal("no-supported-path", actual.AbstentionReason);
		Assert.Null(actual.CategoryId);
	}

	[Fact]
	public void RejectedPathsDoNotCreateAnAssignment()
	{
		CandidateDecision actual = Decide(Scores());
		Assert.Equal("no-supported-path", actual.AbstentionReason);
	}

	[Fact]
	public void ThresholdBoundariesRemainDeterministic()
	{
		CandidateDecision actual = Decide(Scores(("food", .7, .7), ("fruit", .3, .9)), .7, .3);
		Assert.Equal("food", actual.CategoryId);
		Assert.Equal("uncertain-purchase-evidence", Decide(Scores(("food", .9, .9)), .6999).AbstentionReason);
		Assert.Equal("uncovered-alternatives", Decide(Scores(("food", .9, .9)), .9, .3001).AbstentionReason);
	}

	[Fact]
	public void InvalidJudgmentInUnusedBranchIsStillFailure()
	{
		Dictionary<string, CandidatePathJudgment> scores = Scores(("food", .9, .9), ("tools", .1, double.NaN));
		Assert.Equal("invalid-judgment", Decide(scores).Failure);
	}

	[Fact]
	public void PlanSnapshotsCallerCollectionsAndSelectionIsOrderIndependent()
	{
		SuggestionCategory[] taxonomy = Categories.ToArray();
		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(new("APPLES", null, null), "rev", taxonomy, ["apple", "pear", "tools"]);
		taxonomy[0] = new("invented", "Invented", "Invented", null);
		Dictionary<string, CandidatePathJudgment> scores = Scores(("food", .9, .9), ("fruit", .9, .9), ("apple", .9, .9), ("pear", .5, .5));
		Dictionary<string, CandidatePathJudgment> reversed = scores.Reverse().ToDictionary(x => x.Key, x => x.Value);

		Assert.Equal("food", plan.Taxonomy[0].Id);
		Assert.Equal("fruit", SupportedCategoryCandidate.Decide(plan, new("rev", .9, .1, reversed)).CategoryId);
	}

	[Fact]
	public void TaxonomyMustHaveUniqueIdsKnownParentsAndNoCycles()
	{
		SuggestionCategory root = new("root", "Root", "Definition", null);
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Item", null, null), "rev", [root, root], ["root"]));
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Item", null, null), "rev", [root with { ParentId = "unknown" }], ["root"]));
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Item", null, null), "rev", [root with { ParentId = "root" }], ["root"]));
	}

	[Fact]
	public void ExpandedPathsCannotSilentlyExceedBound()
	{
		List<SuggestionCategory> taxonomy = [];
		List<string> shortlist = [];
		for (int branch = 0; branch < 9; branch++)
		{
			for (int depth = 0; depth < 16; depth++)
			{
				taxonomy.Add(new($"b{branch}d{depth}", "Category", "Definition", depth == 0 ? null : $"b{branch}d{depth - 1}"));
			}
			shortlist.Add($"b{branch}d15");
		}
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Item", null, null), "rev", taxonomy, shortlist));
	}

	[Fact]
	public void ExcessivelyDeepTaxonomyIsRejectedBeforeAnyQuestion()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 17)
			.Select(depth => new SuggestionCategory($"d{depth}", "Category", "Definition", depth == 0 ? null : $"d{depth - 1}")).ToArray();
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Item", null, null), "rev", taxonomy, ["d16"]));
	}
	[Fact]
	public void AnswerIdsMustMatchOrdinallyEvenWithCallerComparer()
	{
		Dictionary<string, CandidatePathJudgment> scores = new(Scores(("food", .9, .9)), StringComparer.OrdinalIgnoreCase);
		scores.Remove("food");
		scores.Add("FOOD", new(.9, .9));
		Assert.Equal("invalid-judgment", Decide(scores).Failure);
	}
}


