using System.Text;
using System.Text.Json.Nodes;
using Application.Classification;
using Receipts.JevCandidateParity;

namespace Application.Tests.Classification;

public class ExhaustiveShortlistTests
{
	[Fact]
	public void RetainsCompetingRootsAndUncertainChildrenDespiteRejectedParents()
	{
		ExhaustiveShortlistPlan plan = Prepare([
			new("food", "Food", "Edible goods", null), new("fruit", "Fruit", "Fresh fruit", "food"),
			new("tools", "Tools", "Hand tools", null)]);
		ShortlistResult result = plan.Aggregate(Responses(plan, (id, _) => id.EndsWith("_0") ? .1 : .5));
		Assert.Null(result.Failure);
		Assert.Null(result.AbstentionReason);
		Assert.Equal(new[] { "food", "fruit", "tools" }, result.VerificationPlan!.Paths.Select(path => path.CategoryId));
		Assert.Equal(new[] { "food", "fruit" }, result.VerificationPlan.Paths.Single(path => path.CategoryId == "fruit")
			.Categories.Select(category => category.Id));
	}

	[Fact]
	public void BatchesDeterministicallyWithoutDroppingOrTruncatingAnyPath()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 309).Select(i => new SuggestionCategory($"c{i:D3}",
			$"Category {i}", $"Definition {i}", null)).ToArray();
		ExhaustiveShortlistPlan plan = Prepare(taxonomy);
		ExhaustiveShortlistPlan reversed = Prepare(taxonomy.Reverse().ToArray());
		Assert.Equal(plan.PlanSha256, reversed.PlanSha256);
		Assert.Equal(plan.Batches.Select(x => x.Payload), reversed.Batches.Select(x => x.Payload));
		Assert.Equal(309, plan.Batches.Sum(batch => batch.QuestionIds.Count));
		Assert.Equal(309, plan.Batches.SelectMany(batch => batch.QuestionIds).Distinct().Count());
		Assert.All(plan.Batches, batch =>
		{
			Assert.InRange(batch.QuestionIds.Count, 1, ExhaustiveShortlist.MaximumQuestionsPerBatch);
			Assert.InRange(Encoding.UTF8.GetByteCount(batch.Payload), 1, ExhaustiveShortlist.MaximumBatchBytes);
			Assert.All(JsonNode.Parse(batch.Payload)!["questions"]!.AsObject(), question =>
			{
				Assert.Equal("noul", question.Value!["type"]!.GetValue<string>());
				Assert.StartsWith("Definition", question.Value["instructions"]!["path"]!["categories"]![0]!["definition"]!.GetValue<string>());
			});
		});
	}

	[Theory]
	[InlineData("missing-batch")]
	[InlineData("duplicate-batch")]
	[InlineData("missing-answer")]
	[InlineData("null-value")]
	[InlineData("wrong-plan")]
	[InlineData("duplicate-answer")]
	[InlineData("wrong-model")]
	[InlineData("malformed")]
	[InlineData("extra-envelope")]
	[InlineData("duplicate-envelope")]
	[InlineData("duplicate-usage")]
	[InlineData("negative-usage")]
	[InlineData("overflow-usage")]
	[InlineData("probability-range")]
	[InlineData("oversized")]
	public void IncompleteInvalidOrReplayedResponsesFailWithoutPartialShortlist(string mutation)
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 17).Select(i => new SuggestionCategory($"c{i:D2}", "Goods", "Synthetic goods", null)).ToArray();
		ExhaustiveShortlistPlan plan = Prepare(taxonomy);
		string[] responses = Responses(plan);
		JsonObject first = JsonNode.Parse(responses[0])!.AsObject();
		string key = first["answers"]!.AsObject().First().Key;
		switch (mutation)
		{
			case "missing-batch": responses = responses.Skip(1).ToArray(); break;
			case "duplicate-batch": responses[1] = responses[0]; break;
			case "missing-answer": first["answers"]!.AsObject().Remove(key); responses[0] = first.ToJsonString(); break;
			case "null-value": first["answers"]![key]!["noul"] = null; responses[0] = first.ToJsonString(); break;
			case "wrong-plan": responses = Responses(ExhaustiveShortlist.Prepare(new("Other goods", null, null), "v1", taxonomy, CandidateWire.SupportedEvaluationModel)); break;
			case "duplicate-answer": responses[0] = responses[0].Replace($"\"{key}\":", $"\"{key}\":{{\"type\":\"noul\",\"noul\":0.5}},\"{key}\":"); break;
			case "wrong-model": first["model"] = "jev-latest"; responses[0] = first.ToJsonString(); break;
			case "malformed": responses[0] = "{"; break;
			case "extra-envelope": first["unexpected"] = true; responses[0] = first.ToJsonString(); break;
			case "duplicate-envelope": responses[0] = responses[0].Replace("\"model\":", "\"model\":\"jev-1.14.0\",\"model\":"); break;
			case "duplicate-usage": responses[0] = responses[0].Replace("\"input_tokens\":10", "\"input_tokens\":10,\"input_tokens\":10"); break;
			case "negative-usage": first["usage"]!["input_tokens"] = -1; responses[0] = first.ToJsonString(); break;
			case "overflow-usage": responses[0] = responses[0].Replace("\"input_tokens\":10", "\"input_tokens\":9223372036854775808"); break;
			case "probability-range": first["answers"]![key]!["noul"] = 1.01; responses[0] = first.ToJsonString(); break;
			case "oversized": responses[0] = new string(' ', CandidateWire.MaximumResponseBytes + 1); break;
		}
		ShortlistResult result = plan.Aggregate(responses);
		Assert.Equal("invalid-or-incomplete-shortlist-response", result.Failure);
		Assert.Null(result.VerificationPlan);
		Assert.Null(result.AbstentionReason);
	}

	[Fact]
	public void ByteBoundStartsANewBatchBeforeQuestionCountLimit()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 20).Select(i => new SuggestionCategory($"c{i:D2}",
			"Goods", new string('界', 4096), null)).ToArray();
		ExhaustiveShortlistPlan plan = Prepare(taxonomy);
		Assert.True(plan.Batches.Count > 2);
		Assert.Equal(20, plan.Batches.Sum(batch => batch.QuestionIds.Count));
		Assert.All(plan.Batches, batch =>
		{
			Assert.True(batch.QuestionIds.Count < ExhaustiveShortlist.MaximumQuestionsPerBatch);
			Assert.InRange(Encoding.UTF8.GetByteCount(batch.Payload), 1, ExhaustiveShortlist.MaximumBatchBytes);
		});
	}

	[Fact]
	public void LeafOverflowAbstainsWithoutSelectingTopThirtyTwo()
	{
		ExhaustiveShortlistPlan plan = Prepare(Enumerable.Range(0, 33).Select(i =>
			new SuggestionCategory($"c{i:D2}", "Goods", "Synthetic goods", null)).ToArray());
		ShortlistResult result = plan.Aggregate(Responses(plan));
		Assert.Equal("shortlist-overflow", result.AbstentionReason);
		Assert.Null(result.Failure);
		Assert.Null(result.VerificationPlan);
	}

	[Fact]
	public void AncestorClosureOverflowAbstainsEvenWhenTerminalCountFits()
	{
		List<SuggestionCategory> taxonomy = [];
		for (int branch = 0; branch < 32; branch++)
		{
			for (int depth = 0; depth < 5; depth++)
			{
				taxonomy.Add(new($"b{branch:D2}d{depth}", "Goods", "Synthetic goods", depth == 0 ? null : $"b{branch:D2}d{depth - 1}"));
			}
		}
		ExhaustiveShortlistPlan plan = Prepare(taxonomy.ToArray());
		Assert.Equal("shortlist-overflow", plan.Aggregate(Responses(plan)).AbstentionReason);
	}

	[Fact]
	public void RejectionBoundaryProducesExplicitNoFitAbstention()
	{
		ExhaustiveShortlistPlan plan = Prepare([new("food", "Food", "Edible goods", null)]);
		ShortlistResult result = plan.Aggregate(Responses(plan, (_, _) => .3));
		Assert.Equal("no-plausible-path", result.AbstentionReason);
		Assert.Null(result.VerificationPlan);
	}

	[Fact]
	public void LargeIndividualPathFailsRatherThanTruncatingDefinitions()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 16).Select(i => new SuggestionCategory($"c{i}",
			new string('x', 512), new string('界', 4096), i == 0 ? null : $"c{i - 1}")).ToArray();
		Assert.Throws<ArgumentException>(() => Prepare(taxonomy));
	}

	private static ExhaustiveShortlistPlan Prepare(SuggestionCategory[] taxonomy) =>
		ExhaustiveShortlist.Prepare(new("Synthetic purchase", null, null), "v1", taxonomy, CandidateWire.SupportedEvaluationModel);

	private static string[] Responses(ExhaustiveShortlistPlan plan, Func<string, int, double>? value = null) =>
		plan.Batches.Select(batch =>
		{
			JsonObject answers = new();
			foreach (string id in batch.QuestionIds)
			{
				answers[id] = new JsonObject { ["type"] = "noul", ["noul"] = value?.Invoke(id, batch.Index) ?? .5 };
			}
			return new JsonObject
			{
				["model"] = CandidateWire.SupportedEvaluationModel,
				["answers"] = answers,
				["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
			}.ToJsonString();
		}).ToArray();
}
