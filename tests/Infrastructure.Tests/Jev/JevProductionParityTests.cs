using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Infrastructure.Jev;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public class JevProductionParityTests
{
	[Theory]
	[InlineData(0.3, "no-fit", null)]
	[InlineData(0.5, "uncertain-fit", null)]
	[InlineData(0.7, null, "fruit")]
	[InlineData(1.0, null, "fruit")]
	public void OfflineEvaluationUsesExactProductionQuestionsAndDecision(double fit, string? reason, string? category)
	{
		ProductionParityFixture fixture = Fixture(fit);

		ProductionParityRecord actual = ProductionParity.Evaluate(fixture);

		Assert.Equal(Encoding.UTF8.GetString(JevSuggestionQuestions.Serialize(fixture.Request)), actual.RequestJson);
		Assert.Equal(JsonSerializer.Serialize(CategorySuggestionPolicy.Decide(fixture.Request, fixture.Judgment, "jev"), ProductionParity.JsonOptions),
			JsonSerializer.Serialize(actual.Decision, ProductionParity.JsonOptions));
		Assert.Equal(reason, actual.Decision.Suggestion!.AbstentionReason);
		Assert.Equal(category, actual.Decision.Suggestion.CategoryId);
		Assert.Equal("classification-questions-v5", actual.QuestionPolicy);
		Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual.RequestJson))), actual.QuestionSha256);
		Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(actual.Decision, ProductionParity.JsonOptions))), actual.DecisionSha256);
		Assert.Equal(JsonSerializer.Serialize(actual, ProductionParity.JsonOptions),
			JsonSerializer.Serialize(ProductionParity.Evaluate(fixture), ProductionParity.JsonOptions));
	}

	[Fact]
	public void InvalidJudgmentRemainsAProductionFailure()
	{
		ProductionParityFixture fixture = Fixture(0.9);
		fixture = fixture with { Judgment = fixture.Judgment with { Model = "wrong-model" } };

		ProductionParityRecord actual = ProductionParity.Evaluate(fixture);

		Assert.Null(actual.Decision.Suggestion);
		Assert.Equal(SuggestionFailure.InvalidResponse, actual.Decision.Failure);
	}

	[Fact]
	public void InvalidRequestIsRejectedBeforeSerialization()
	{
		ProductionParityFixture fixture = Fixture(0.9);
		fixture = fixture with { Request = fixture.Request with { Categories = [] } };

		Assert.Throws<ArgumentException>(() => ProductionParity.Evaluate(fixture));
	}

	[Fact]
	public void ParentFallbackUsesProductionHierarchyPolicy()
	{
		ProductionParityFixture fixture = Fixture(0.9);
		fixture = fixture with
		{
			Judgment = fixture.Judgment with
			{
				Choices = new Dictionary<string, CategoryChoiceJudgment>
				{
					["category"] = new("food", 0.8, new Dictionary<string, double> { ["food"] = 1 }),
					["branch_0"] = new("food", 0.8, new Dictionary<string, double> { ["food"] = 0.8, ["fruit"] = 0.2 })
				}
			}
		};

		ProductionParityRecord actual = ProductionParity.Evaluate(fixture);

		Assert.Equal("food", actual.Decision.Suggestion!.CategoryId);
		Assert.Equal(["food"], actual.Decision.Suggestion.Path);
	}

	[Fact]
	public void DescriptionChangesQuestionFingerprintButNotInjectedDecision()
	{
		ProductionParityFixture fixture = Fixture(0.9);
		ProductionParityRecord expected = ProductionParity.Evaluate(fixture);

		ProductionParityRecord actual = ProductionParity.Evaluate(fixture with { Request = fixture.Request with { Description = "synthetic pear" } });

		Assert.NotEqual(expected.QuestionSha256, actual.QuestionSha256);
		Assert.Equal(expected.DecisionSha256, actual.DecisionSha256);
	}

	[Fact]
	public void FixtureRoundTripRequiresNoProviderOrCredential()
	{
		string json = JsonSerializer.Serialize(Fixture(0.9), ProductionParity.JsonOptions);
		ProductionParityFixture fixture = JsonSerializer.Deserialize<ProductionParityFixture>(json, ProductionParity.JsonOptions)!;

		ProductionParityRecord actual = ProductionParity.Evaluate(fixture);

		Assert.Equal("fruit", actual.Decision.Suggestion!.CategoryId);
		Assert.Equal(["food", "fruit"], actual.Decision.Suggestion.Path);
		Assert.Equal("local_choice", actual.Decision.Suggestion.ProbabilityScope);
	}

	private static ProductionParityFixture Fixture(double fit) => new("synthetic-fruit",
		new("synthetic apple", "synthetic-taxonomy-v1", [new("food", "Food", "Edible purchases", null), new("fruit", "Fruit", "Fresh fruit", "food")]),
		new(CategorySuggestionPolicy.Model, "synthetic-taxonomy-v1", fit, new Dictionary<string, CategoryChoiceJudgment>
		{
			["category"] = new("food", 0.8, new Dictionary<string, double> { ["food"] = 1 }),
			["branch_0"] = new("fruit", 0.8, new Dictionary<string, double> { ["food"] = 0.1, ["fruit"] = 0.9 })
		}, 100, 10));
}
