using System.Text;
using System.Text.Json.Nodes;
using Application.Classification;
using Infrastructure.Jev;

namespace Infrastructure.Tests.Jev;

public class JevSelectedPathQuestionsTests
{
	private static readonly CategorySuggestionRequest Request = new("paper", "rev", [
		new("root", "Office", "Office goods", null), new("leaf", "Paper", "Sheets", "root"),
		new("other", "Food", "Food goods", null)]);
	private static readonly CategorySuggestion Baseline = new("leaf", ["root", "leaf"], null, .9, .8, .7,
		new("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version, "rev", 12, 3), "local_choice");

	private static byte[] Response(JevSelectedPathQuestions.Prepared prepared, double probability = .9) =>
		Encoding.UTF8.GetBytes(new JsonObject
		{
			["model"] = CategorySuggestionPolicy.Model,
			["answers"] = new JsonObject { [prepared.QuestionId] = new JsonObject { ["type"] = "noul", ["noul"] = probability } },
			["usage"] = new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 3 }
		}.ToJsonString());

	[Fact]
	public void PayloadMatchesEvaluatedExperimentExceptForBoundQuestionId()
	{
		var prepared = JevSelectedPathQuestions.Prepare(Request, Baseline);
		var actual = JsonNode.Parse(prepared.Payload)!;
		// Captured directly from PurchaseEvidenceExperiment.PrepareSelectedPath with the same synthetic input.
		var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jev-selected-path-experiment.json")))!;
		var actualQuestions = actual["questions"]!.AsObject();
		var expectedQuestions = expected["questions"]!.AsObject();
		var actualAnswer = Assert.Single(actualQuestions).Value!.DeepClone();
		var expectedAnswer = Assert.Single(expectedQuestions).Value!.DeepClone();
		actualQuestions.Clear(); expectedQuestions.Clear();
		actualQuestions["question"] = actualAnswer; expectedQuestions["question"] = expectedAnswer;
		Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected: {expected}\nActual: {actual}");
	}

	[Theory]
	[InlineData(.7)]
	[InlineData(.2)]
	public void ParsesExactTypedNoulAndUsage(double probability)
	{
		var prepared = JevSelectedPathQuestions.Prepare(Request, Baseline);
		var result = prepared.ParseResponse(Response(prepared, probability));
		Assert.NotNull(result);
		Assert.Equal(probability, result.Probability);
		Assert.Equal(12, result.InputTokens);
		Assert.Equal(3, result.OutputTokens);
	}

	[Fact]
	public void ReplaysFromDifferentPurchasePathTaxonomyOrBaselineFailClosed()
	{
		var original = JevSelectedPathQuestions.Prepare(Request, Baseline);
		var otherPath = Baseline with { CategoryId = "other", Path = ["other"] };
		var variants = new[] {
			JevSelectedPathQuestions.Prepare(Request with { Description = "other purchase" }, Baseline),
			JevSelectedPathQuestions.Prepare(Request, otherPath),
			JevSelectedPathQuestions.Prepare(Request with { Categories = [.. Request.Categories.Select(c => c.Id == "other" ? c with { Definition = "Changed" } : c)] }, Baseline),
			JevSelectedPathQuestions.Prepare(Request, Baseline with { FitProbability = .8 }),
			JevSelectedPathQuestions.Prepare(Request, Baseline with { CategoryProbability = .6 })
		};
		foreach (var variant in variants)
		{
			Assert.NotEqual(original.QuestionId, variant.QuestionId);
			Assert.Null(variant.ParseResponse(Response(original)));
		}
	}

	[Fact]
	public void RejectsMalformedDuplicateMissingExtraWrongTypeAndOutOfRangeFields()
	{
		var prepared = JevSelectedPathQuestions.Prepare(Request, Baseline);
		string valid = Encoding.UTF8.GetString(Response(prepared));
		string id = prepared.QuestionId;
		string[] invalid = ["null", "{", valid.Replace("\"noul\":0.9", "\"noul\":2"),
			valid.Replace("\"type\":\"noul\"", "\"type\":\"choice\""),
			valid.Replace("\"noul\":0.9", "\"noul\":0.9,\"noul\":0.1"),
			valid.Replace($"\"{id}\"", "\"wrong\""),
			valid.Replace("\"output_tokens\":3", "\"output_tokens\":-1"),
			valid.Replace("\"output_tokens\":3", "\"output_tokens\":3,\"extra\":1"),
			valid.Replace(",\"output_tokens\":3", ""),
			valid.Replace("\"model\":\"jev-1.14.0\"", "\"model\":\"wrong\"")];
		foreach (string raw in invalid)
		{
			Assert.Null(prepared.ParseResponse(Encoding.UTF8.GetBytes(raw)));
		}
	}

	[Fact]
	public void RejectsInvalidBaselineAndReturnsIndependentPayloadCopy()
	{
		Assert.Throws<ArgumentException>(() => JevSelectedPathQuestions.Prepare(Request, Baseline with { CategoryId = "missing" }));
		Assert.Throws<ArgumentException>(() => JevSelectedPathQuestions.Prepare(Request, Baseline with { FitProbability = double.NaN }));
		var prepared = JevSelectedPathQuestions.Prepare(Request, Baseline);
		byte[] first = prepared.Payload;
		first[0] = 0;
		Assert.NotEqual(first[0], prepared.Payload[0]);
	}
}
