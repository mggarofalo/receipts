using System.Text.Json.Nodes;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Application.Tests.Classification;

public class SelectedPathExperimentTests
{
	private static CandidateVerificationPlan Plan(string description = "Bread") => SupportedCategoryCandidate.Plan(
			new(description, "Market", null), "synthetic-v1",
			[new("food", "Food", "Food for any use", null),
				 new("business", "Business food", "Food for business use", "food"),
				 new("fruit", "Fruit", "Fruit for any use", "food")], ["business", "fruit"]);

	private static string Response(SelectedPathExperimentRequest request, double probability) => new JsonObject
	{
		["model"] = CandidateWire.SupportedEvaluationModel,
		["answers"] = new JsonObject { [request.QuestionPaths.Keys.Single()] = new JsonObject { ["type"] = "noul", ["noul"] = probability } },
		["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 }
	}.ToJsonString();

	private static SelectedPathExperimentRequest Prepare(string id = "business", double global = .84) =>
			PurchaseEvidenceExperiment.PrepareSelectedPath(Plan(), id, global, CandidateWire.SupportedEvaluationModel);

	[Fact]
	public void RequestUsesOnlySelectedFullPathAndPurchaseEvidence()
	{
		var request = Prepare();
		var payload = JsonNode.Parse(request.Payload)!;
		var question = Assert.Single(payload["questions"]!.AsObject());
		Assert.Equal("noul", question.Value!["type"]!.GetValue<string>());
		var path = question.Value!["instructions"]!["path"]!["categories"]!.AsArray();
		Assert.Equal(new[] { "food", "business" }, path.Select(p => p!["id"]!.GetValue<string>()));
		Assert.Equal("Food for business use", path[1]!["definition"]!.GetValue<string>());
		Assert.Equal("Bread", payload["state"]!["purchase"]!["description"]!.GetValue<string>());
		Assert.Equal("business", request.QuestionPaths.Single().Value);
		Assert.DoesNotContain("globalFit", request.Payload);
	}

	[Theory]
	[InlineData(.1, "unsupported-selected-path")]
	[InlineData(.5, "uncertain-selected-path")]
	public void HighGlobalFitCannotAuthorizeWrongOrAmbiguousChild(double support, string reason)
	{
		var request = Prepare();
		var result = request.ParseResponse(Response(request, support));
		Assert.Null(result.CategoryId);
		Assert.Equal(reason, result.AbstentionReason);
		Assert.Equal(.84, result.GlobalFitProbability);
		Assert.Equal(support, result.SelectedPathSupportProbability);
		Assert.Null(result.Failure);
	}

	[Fact]
	public void SupportedSpecificPathRequiresBothIndependentJudgments()
	{
		var request = Prepare("fruit");
		Assert.Equal("fruit", request.ParseResponse(Response(request, .9)).CategoryId);
		var uncertainGlobal = Prepare("fruit", .5);
		var result = uncertainGlobal.ParseResponse(Response(uncertainGlobal, .9));
		Assert.Null(result.CategoryId);
		Assert.Equal("uncertain-global-fit", result.AbstentionReason);
		// No parent fallback can silently introduce a different level of specificity.
		Assert.Null(Prepare("fruit").ParseResponse(Response(request, .1)).CategoryId);
	}

	[Fact]
	public void InvalidAndReplayedResponsesFailClosed()
	{
		var request = Prepare();
		string valid = Response(request, .9);
		string id = request.QuestionPaths.Keys.Single();
		foreach (string invalid in new[] { "null", "{", valid.Replace("\"noul\":0.9", "\"noul\":2"),
						valid.Replace("\"type\":\"noul\"", "\"type\":\"choice\""),
						valid.Replace($"\"{id}\":", $"\"{id}\":{{\"type\":\"noul\",\"noul\":0.9}},\"{id}\":"),
						Response(Prepare("fruit"), .9) })
		{
			var result = request.ParseResponse(invalid);
			Assert.Equal("invalid-response", result.Failure);
			Assert.Null(result.CategoryId);
			Assert.Null(result.SelectedPathSupportProbability);
		}
		Assert.Throws<ArgumentException>(() => Prepare("unknown"));
		Assert.Throws<ArgumentException>(() => Prepare(global: double.NaN));
	}
}
