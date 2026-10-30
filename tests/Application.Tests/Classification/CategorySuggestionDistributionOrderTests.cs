using Application.Classification;

namespace Application.Tests.Classification;

public class CategorySuggestionDistributionOrderTests
{
	public static IEnumerable<object[]> BoundaryOrders()
	{
		int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
		foreach (int stage in new[] { 1, 2 })
		{
			foreach (double last in new[] { 0.15, 0.17 })
			{
				foreach (int[] order in orders)
				{
					yield return [stage, last, order];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(BoundaryOrders))]
	public void InclusiveSumToleranceAcceptsSameDistributionInEveryInsertionOrder(int stage, double last, int[] order)
	{
		var result = Decide(stage, last, order);
		Assert.Null(result.Failure);
		Assert.NotNull(result.Suggestion);
		Assert.Equal("fake0", result.Suggestion.CategoryId);
		Assert.Equal(stage == 1 ? new[] { "fake0" } : ["root", "fake0"], result.Suggestion.Path);
		Assert.Equal(0.5, result.Suggestion.CategoryProbability);
	}

	[Theory]
	[InlineData(1, 0.1499)]
	[InlineData(1, 0.1701)]
	[InlineData(2, 0.1499)]
	[InlineData(2, 0.1701)]
	public void DistributionGenuinelyOutsideSumToleranceRemainsInvalid(int stage, double last)
	{
		var result = Decide(stage, last, [0, 1, 2]);
		Assert.Equal(SuggestionFailure.InvalidResponse, result.Failure);
		Assert.Null(result.Suggestion);
	}

	// Stage 1 chooses among three roots; stage 2 among three children of a single root.
	private static CategorySuggestionResult Decide(int stage, double last, int[] order)
	{
		string[] ids = ["fake0", "fake1", "fake2"];
		double[] values = [0.5, 0.34, last];
		var probabilities = order.ToDictionary(index => ids[index], index => values[index]);
		if (stage == 1)
		{
			var flat = new CategorySuggestionRequest("Synthetic diagnostic", "synthetic-revision",
				[new("fake0", "Root", "Synthetic", null), new("fake1", "First", "Synthetic", null), new("fake2", "Second", "Synthetic", null)]);
			return CategorySuggestionPolicy.DecideStage1(flat,
				new CategoryStageJudgment(CategorySuggestionPolicy.Model, "fake0", 0.8, probabilities, 0.8, 1, 0), "test");
		}

		var request = new CategorySuggestionRequest("Synthetic diagnostic", "synthetic-revision",
			[new("root", "Root", "Synthetic", null), new("fake0", "Zero", "Synthetic", "root"),
				new("fake1", "First", "Synthetic", "root"), new("fake2", "Second", "Synthetic", "root")]);
		var baseline = CategorySuggestionPolicy.DecideStage1(request, new CategoryStageJudgment(CategorySuggestionPolicy.Model,
			"root", 0.8, new Dictionary<string, double> { ["root"] = 1 }, 0.8, 1, 0), "test").Suggestion!;
		return CategorySuggestionPolicy.DecideStage2(request, baseline,
			new CategoryStageJudgment(CategorySuggestionPolicy.Model, "fake0", 0.8, probabilities, null, 1, 0));
	}
}
