using Application.Classification;

namespace Application.Tests.Classification;

public class CategorySuggestionDistributionOrderTests
{
	public static IEnumerable<object[]> BoundaryOrders()
	{
		int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
		foreach (int mode in new[] { 0, 1, 2 })
		{
			foreach (double last in new[] { 0.15, 0.17 })
			{
				foreach (int[] order in orders)
				{
					yield return [mode, last, order];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(BoundaryOrders))]
	public void InclusiveSumToleranceAcceptsSameDistributionInEveryInsertionOrder(int mode, double last, int[] order)
	{
		var result = Decide(mode, last, order);
		Assert.Null(result.Failure);
		Assert.NotNull(result.Suggestion);
		Assert.Equal("fake0", result.Suggestion.CategoryId);
		Assert.Equal(new[] { "fake0" }, result.Suggestion.Path);
		Assert.Equal(mode == 1 ? 1.0 : 0.5, result.Suggestion.CategoryProbability);
	}

	[Theory]
	[InlineData(0, 0.1499)]
	[InlineData(0, 0.1701)]
	[InlineData(1, 0.1499)]
	[InlineData(1, 0.1701)]
	[InlineData(2, 0.1499)]
	[InlineData(2, 0.1701)]
	public void DistributionGenuinelyOutsideSumToleranceRemainsInvalid(int mode, double last)
	{
		var result = Decide(mode, last, [0, 1, 2]);
		Assert.Equal(SuggestionFailure.InvalidResponse, result.Failure);
		Assert.Null(result.Suggestion);
	}

	// Modes exercise the flat overload, hierarchy root choice, and hierarchy branch choice.
	private static CategorySuggestionResult Decide(int mode, double last, int[] order)
	{
		string[] ids = ["fake0", "fake1", "fake2"];
		double[] values = [0.5, 0.34, last];
		var probabilities = order.ToDictionary(index => ids[index], index => values[index]);
		SuggestionCategory[] categories = mode == 2
			? [new("fake0", "Root", "Synthetic", null), new("fake1", "First", "Synthetic", "fake0"), new("fake2", "Second", "Synthetic", "fake0")]
			: [new("fake0", "Root", "Synthetic", null), new("fake1", "First", "Synthetic", null), new("fake2", "Second", "Synthetic", null)];
		if (mode == 1)
		{
			categories = [.. categories, new("child", "Child", "Synthetic", "fake0")];
		}

		var request = new CategorySuggestionRequest("Synthetic diagnostic", "synthetic-revision", categories);
		if (mode == 0)
		{
			var judgment = new CategoryJudgment(CategorySuggestionPolicy.Model, request.TaxonomyRevision,
				"fake0", 0.8, 0.8, probabilities, 1, 0);
			return CategorySuggestionPolicy.Decide(request, judgment, "test");
		}
		var choices = new Dictionary<string, CategoryChoiceJudgment>
		{
			["category"] = mode == 2
				? new("fake0", 0.8, new Dictionary<string, double> { ["fake0"] = 1 })
				: new("fake0", 0.8, probabilities),
			["branch_0"] = mode == 2
				? new("fake0", 0.8, probabilities)
				: new("fake0", 0.8, new Dictionary<string, double> { ["fake0"] = 1, ["child"] = 0 })
		};
		return CategorySuggestionPolicy.Decide(request,
			new CategoryHierarchyJudgment(CategorySuggestionPolicy.Model, request.TaxonomyRevision, 0.8, choices, 1, 0), "test");
	}
}
