using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification;

namespace Application.Tests.Classification;

public class CategoryAutoWritePolicyTests
{
	private static readonly CategorySuggestionRequest Request = new("gala apples", "revision-1",
	[
		new("fruit", "Fruit", "Fresh or dried fruit", null),
		new("apples", "Apples", "Apples of any variety", "fruit"),
		new("dairy", "Dairy", "Dairy", null),
		new("milk", "Milk", "Milk", "dairy")
	]);

	// A completed stage-2 result: strong fit and root, with the given top leaf.
	private static CategorySuggestion Completed(double root = 0.95, string leaf = "apples", double leafProbability = 0.95, double fit = 0.9) =>
		new("fruit", ["fruit"], null, fit, root, 0.8,
			new("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version, Request.TaxonomyRevision, 10, 5), "local_choice",
			RootCandidates: [new("fruit", root), new("dairy", 1 - root)],
			LeafCandidates: [new(leaf, leafProbability), new("milk", 1 - leafProbability)]);

	private static CategoryAutoWriteOptions Options(bool leafEnabled = false) => new() { LeafAutoWriteEnabled = leafEnabled };

	[Fact]
	public void DefaultsWriteRootsAtNinetyPercentAndKeepLeafWritesOff()
	{
		CategoryAutoWriteOptions options = new();

		Assert.Equal(0.90, options.RootAutoWriteThreshold);
		Assert.Equal(0.90, options.LeafAutoWriteThreshold);
		Assert.False(options.LeafAutoWriteEnabled);
		Assert.True(options.IsValid());
	}

	[Fact]
	public void RootAtThresholdWritesTheRoot()
	{
		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(Request, Completed(root: 0.90), Options());

		Assert.Equal(new CategoryAutoWriteDecision("fruit", CategoryAutoWriteLevel.Root, 0.9, 0.90, 0.95), decision);
	}

	[Fact]
	public void RootBelowThresholdStaysASuggestion() =>
		Assert.Null(CategoryAutoWritePolicy.Decide(Request, Completed(root: 0.89), Options()));

	[Fact]
	public void FitBelowSuggestionGateNeverWrites() =>
		Assert.Null(CategoryAutoWritePolicy.Decide(Request, Completed(fit: 0.69), Options()));

	[Fact]
	public void DisabledLeafWritesKeepTheRootEvenWhenTheLeafIsStrong()
	{
		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(Request, Completed(leafProbability: 0.99), Options());

		Assert.Equal("fruit", decision!.CategoryId);
		Assert.Equal(CategoryAutoWriteLevel.Root, decision.Level);
	}

	[Fact]
	public void EnabledStrongLeafUnderTheSameRootWritesTheLeaf()
	{
		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(Request, Completed(leafProbability: 0.90), Options(leafEnabled: true));

		Assert.Equal(new CategoryAutoWriteDecision("apples", CategoryAutoWriteLevel.Leaf, 0.9, 0.95, 0.90), decision);
	}

	[Fact]
	public void EnabledLeafBelowThresholdWritesTheRoot()
	{
		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(Request, Completed(leafProbability: 0.89), Options(leafEnabled: true));

		Assert.Equal(CategoryAutoWriteLevel.Root, decision!.Level);
		Assert.Equal("fruit", decision.CategoryId);
	}

	[Fact]
	public void EnabledLeafUnderAnotherRootWritesTheRoot()
	{
		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(Request, Completed(leaf: "milk", leafProbability: 0.95),
			Options(leafEnabled: true));

		Assert.Equal(CategoryAutoWriteLevel.Root, decision!.Level);
		Assert.Equal("fruit", decision.CategoryId);
	}

	public static TheoryData<string> IncompleteSuggestions() => ["null", "abstention", "no-leaves", "no-roots", "other-policy", "unknown-root", "unknown-leaf"];

	[Theory]
	[MemberData(nameof(IncompleteSuggestions))]
	public void AbstentionsAndIncompleteResultsNeverWrite(string defect)
	{
		CategorySuggestion valid = Completed();
		CategorySuggestion? suggestion = defect switch
		{
			"null" => null,
			"abstention" => valid with { CategoryId = null, Path = [], AbstentionReason = "uncertain-fit" },
			"no-leaves" => valid with { LeafCandidates = null },
			"no-roots" => valid with { RootCandidates = [] },
			"other-policy" => valid with { Provenance = valid.Provenance with { QuestionPolicy = "classification-questions-v6" } },
			"unknown-root" => valid with { RootCandidates = [new("absent", 0.95)] },
			_ => valid with { LeafCandidates = [new("absent", 0.95)] }
		};

		Assert.Null(CategoryAutoWritePolicy.Decide(Request, suggestion, Options(leafEnabled: true)));
	}

	[Fact]
	public void LeafCandidateThatIsARootStillCountsAsUnderThatRoot()
	{
		// A childless root offered in stage 2 as itself: with leaf writes on it is written at leaf level.
		CategorySuggestionRequest request = Request with { Categories = [.. Request.Categories, new("bakery", "Bakery", "Bread", null)] };
		CategorySuggestion suggestion = Completed() with
		{
			CategoryId = "bakery",
			Path = ["bakery"],
			RootCandidates = [new("bakery", 0.95), new("fruit", 0.05)],
			LeafCandidates = [new("bakery", 0.95), new("apples", 0.05)]
		};

		CategoryAutoWriteDecision? decision = CategoryAutoWritePolicy.Decide(request, suggestion, Options(leafEnabled: true));

		Assert.Equal("bakery", decision!.CategoryId);
	}

	[Theory]
	[InlineData(0.5)]
	[InlineData(0.69)]
	[InlineData(1.01)]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	public void InvalidThresholdsFailClosed(double threshold)
	{
		CategoryAutoWriteOptions root = new() { RootAutoWriteThreshold = threshold };
		CategoryAutoWriteOptions leaf = new() { LeafAutoWriteThreshold = threshold, LeafAutoWriteEnabled = true };

		Assert.False(root.IsValid());
		Assert.False(leaf.IsValid());
		Assert.Null(CategoryAutoWritePolicy.Decide(Request, Completed(root: 1.0), root));
		Assert.Null(CategoryAutoWritePolicy.Decide(Request, Completed(root: 1.0), leaf));
	}

	[Theory]
	[InlineData(0.7)]
	[InlineData(1.0)]
	public void ThresholdsBetweenSuggestionGateAndOneAreValid(double threshold) =>
		Assert.True(new CategoryAutoWriteOptions { RootAutoWriteThreshold = threshold, LeafAutoWriteThreshold = threshold }.IsValid());

	[Fact]
	public void InvalidRequestNeverWrites() =>
		Assert.Null(CategoryAutoWritePolicy.Decide(Request with { Description = " " }, Completed(), Options()));

	private static JevAutoWriteEvidence Evidence(CategoryAutoWriteLevel level) => new(Guid.NewGuid(), Guid.NewGuid(),
		CategorySuggestionPolicy.Version, CategorySuggestionPolicy.Model, "revision-1", level, 0.9, 0.95, 0.95, 0.90, 0.90, false);

	[Theory]
	[InlineData(CategoryAutoWriteLevel.Root, "Root")]
	[InlineData(CategoryAutoWriteLevel.Leaf, "Leaf")]
	public void EvidenceRecordsTheLevelByName(CategoryAutoWriteLevel level, string name)
	{
		var evidence = Evidence(level);

		string json = JsonSerializer.Serialize(evidence);

		Assert.Equal(name, JsonNode.Parse(json)!["Level"]!.GetValue<string>());
		Assert.Equal(evidence, JsonSerializer.Deserialize<JevAutoWriteEvidence>(json));
	}

	[Theory]
	[InlineData(0, CategoryAutoWriteLevel.Root)]
	[InlineData(1, CategoryAutoWriteLevel.Leaf)]
	public void EvidenceWrittenWithANumericLevelStillReads(int ordinal, CategoryAutoWriteLevel level)
	{
		var evidence = Evidence(level);
		var legacy = JsonNode.Parse(JsonSerializer.Serialize(evidence))!.AsObject();
		legacy["Level"] = ordinal;

		Assert.Equal(evidence, JsonSerializer.Deserialize<JevAutoWriteEvidence>(legacy.ToJsonString()));
	}
}
