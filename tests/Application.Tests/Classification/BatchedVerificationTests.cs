using System.Text;
using System.Text.Json.Nodes;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Application.Tests.Classification;

public class BatchedVerificationTests
{
	[Fact]
	public void SeventyTerminalsWith112ClosureNodesReachGlobalPolicyWithoutPruning()
	{
		SuggestionCategory[] taxonomy = LargeTaxonomy();
		ExhaustiveShortlistPlan shortlist = ExhaustiveShortlist.PrepareForBatchedVerification(new("Synthetic goods", null, null), "v1", taxonomy, CandidateWire.SupportedEvaluationModel);
		ShortlistResult result = shortlist.Aggregate(shortlist.Batches.Select(batch => Response(batch.QuestionIds, _ => .5)).ToArray());
		Assert.Null(result.Failure);
		Assert.Null(result.AbstentionReason);
		Assert.Equal(112, result.VerificationPlan!.Paths.Count);
		BatchedVerificationPlan verification = BatchedVerification.Prepare(result.VerificationPlan, CandidateWire.SupportedEvaluationModel);
		Assert.Equal(1, verification.Batches.Count(batch => batch.IsGlobalCoverage));
		Assert.Equal(226, verification.Batches.Sum(batch => batch.QuestionIds.Count));
		Assert.Equal(226, verification.Batches.SelectMany(batch => batch.QuestionIds).Distinct().Count());
		JsonNode global = JsonNode.Parse(verification.Batches[0].Payload)!;
		Assert.Equal(112, global["state"]!["taxonomy"]!.AsArray().Count);
		Assert.Equal(112, global["state"]!["paths"]!.AsArray().Count);
		Assert.All(verification.Batches, batch => Assert.InRange(Encoding.UTF8.GetByteCount(batch.Payload), 1, BatchedVerification.MaximumBatchBytes));
		Assert.Equal("conflicting-paths", verification.Aggregate(Responses(verification)).Decision.AbstentionReason);
		Assert.NotNull(verification.Aggregate(Responses(verification).SkipLast(1).ToArray()).Decision.Failure);
		BatchedVerificationPlan reversed = BatchedVerification.Prepare(SupportedCategoryCandidate.PlanForBatchedVerification(new("Synthetic goods", null, null), "v1", taxonomy.Reverse().ToArray(), taxonomy.Where(x => x.Id.StartsWith("leaf")).Select(x => x.Id).Reverse().ToArray()), CandidateWire.SupportedEvaluationModel);
		Assert.Equal(verification.PlanSha256, reversed.PlanSha256);
	}

	[Fact]
	public void GlobalUnionConflictAndAncestorFallbackUseExistingPolicy()
	{
		SuggestionCategory[] taxonomy = [new("root", "Goods", "Goods", null), new("a", "A", "A goods", "root"), new("b", "B", "B goods", "root")];
		BatchedVerificationPlan plan = Prepare(taxonomy, ["a", "b"]);
		CandidateDecision decision = plan.Aggregate(Responses(plan, id => id.EndsWith("_unlisted") ? .1 : id.Contains("path_2_") || id.EndsWith("_purchase") ? .9 : .5)).Decision;
		Assert.Equal("root", decision.CategoryId);
		Assert.Equal("uncovered-alternatives", plan.Aggregate(Responses(plan, _ => .9)).Decision.AbstentionReason);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("duplicate")]
	[InlineData("omit-answer")]
	[InlineData("duplicate-answer")]
	[InlineData("other-purchase")]
	[InlineData("other-taxonomy")]
	[InlineData("wrong-model")]
	[InlineData("malformed")]
	[InlineData("null")]
	[InlineData("out-of-range")]
	[InlineData("negative-usage")]
	public void InvalidOrIncompleteResponseNeverProducesAWinner(string mutation)
	{
		SuggestionCategory[] taxonomy = [new("root", "Goods", "Goods", null)];
		BatchedVerificationPlan plan = Prepare(taxonomy, ["root"]);
		string[] responses = Responses(plan);
		JsonObject first = JsonNode.Parse(responses[0])!.AsObject();
		string id = plan.Batches[0].QuestionIds[0];
		switch (mutation)
		{
			case "missing": responses = responses.SkipLast(1).ToArray(); break;
			case "duplicate": responses[1] = responses[0]; break;
			case "omit-answer": first["answers"]!.AsObject().Remove(id); responses[0] = first.ToJsonString(); break;
			case "duplicate-answer": responses[0] = responses[0].Replace($"\"{id}\":", $"\"{id}\":{{\"type\":\"noul\",\"noul\":0.9}},\"{id}\":"); break;
			case "other-purchase": responses = Responses(Prepare(taxonomy, ["root"], "Other purchase")); break;
			case "other-taxonomy": responses = Responses(Prepare([taxonomy[0] with { Definition = "Other goods" }], ["root"])); break;
			case "wrong-model": first["model"] = "jev-latest"; responses[0] = first.ToJsonString(); break;
			case "malformed": responses[0] = "{"; break;
			case "null": first["answers"]![id]!["noul"] = null; responses[0] = first.ToJsonString(); break;
			case "out-of-range": first["answers"]![id]!["noul"] = 2; responses[0] = first.ToJsonString(); break;
			case "negative-usage": first["usage"]!["input_tokens"] = -1; responses[0] = first.ToJsonString(); break;
		}
		CandidateWireResult result = plan.Aggregate(responses);
		Assert.NotNull(result.Decision.Failure);
		Assert.Null(result.Decision.CategoryId);
		Assert.Null(result.Judgment);
	}

	[Fact]
	public void ClosureOverflowAndLegacyTerminalLimitFailWithoutPruning()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 129).Select(i => new SuggestionCategory($"c{i}", "Goods", "Goods", null)).ToArray();
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.PlanForBatchedVerification(new("Goods", null, null), "v1", taxonomy, taxonomy.Select(x => x.Id).ToArray()));
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.Plan(new("Goods", null, null), "v1", taxonomy, taxonomy.Take(33).Select(x => x.Id).ToArray()));
		ExhaustiveShortlistPlan plan = ExhaustiveShortlist.PrepareForBatchedVerification(new("Goods", null, null), "v1", taxonomy, CandidateWire.SupportedEvaluationModel);
		Assert.Equal("shortlist-overflow", plan.Aggregate(plan.Batches.Select(batch => Response(batch.QuestionIds, _ => .5)).ToArray()).AbstentionReason);
	}

	[Fact]
	public void TokenBudgetMustCoverEveryRequestIncludingGlobalCoverage()
	{
		BatchedVerificationPlan plan = Prepare([new("root", "Goods", "Goods", null)], ["root"]);
		int calls = 0;
		plan.ValidateInputTokenBudget(_ => { calls++; return 100; }, 100);
		Assert.Equal(plan.Batches.Count, calls);
		Assert.Throws<ArgumentException>(() => plan.ValidateInputTokenBudget(_ => 101, 100));
		Assert.Throws<ArgumentException>(() => plan.ValidateInputTokenBudget(_ => 0, 100));
	}

	[Fact]
	public void AncestorClosureOverflowFailsWithFewerThan128Terminals()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 65).Select(i => new SuggestionCategory($"root{i}", "Goods", "Goods", null))
			.Concat(Enumerable.Range(0, 65).Select(i => new SuggestionCategory($"leaf{i}", "Goods", "Goods", $"root{i}"))).ToArray();
		Assert.Throws<ArgumentException>(() => SupportedCategoryCandidate.PlanForBatchedVerification(new("Goods", null, null), "v1", taxonomy, taxonomy.Where(x => x.ParentId != null).Select(x => x.Id).ToArray()));
	}

	[Fact]
	public void OversizedGlobalTaxonomyFailsWithoutTruncation()
	{
		SuggestionCategory[] taxonomy = Enumerable.Range(0, 30).Select(i => new SuggestionCategory($"c{i}", "Goods", new string('界', 4096), null)).ToArray();
		Assert.Throws<ArgumentException>(() => Prepare(taxonomy, ["c0"]));
	}

	private static SuggestionCategory[] LargeTaxonomy() => Enumerable.Range(0, 42).Select(i => new SuggestionCategory($"root{i:D2}", "Goods", "Goods", null))
		.Concat(Enumerable.Range(0, 70).Select(i => new SuggestionCategory($"leaf{i:D2}", "Goods", "Goods", $"root{i % 42:D2}"))).ToArray();

	private static BatchedVerificationPlan Prepare(SuggestionCategory[] taxonomy, string[] ids, string purchase = "Synthetic goods") =>
		BatchedVerification.Prepare(SupportedCategoryCandidate.PlanForBatchedVerification(new(purchase, null, null), "v1", taxonomy, ids), CandidateWire.SupportedEvaluationModel);

	private static string[] Responses(BatchedVerificationPlan plan, Func<string, double>? value = null) =>
		plan.Batches.Select(batch => Response(batch.QuestionIds, value ?? (id => id.EndsWith("_unlisted") ? .1 : .9))).ToArray();

	private static string Response(IReadOnlyList<string> ids, Func<string, double> value)
	{
		JsonObject answers = new();
		foreach (string id in ids)
		{
			answers[id] = new JsonObject { ["type"] = "noul", ["noul"] = value(id) };
		}

		return new JsonObject
		{
			["model"] = CandidateWire.SupportedEvaluationModel,
			["answers"] = answers,
			["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
		}.ToJsonString();
	}
}
