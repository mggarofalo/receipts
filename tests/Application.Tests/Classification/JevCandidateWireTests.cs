using System.Text.Json.Nodes;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Application.Tests.Classification;

public class JevCandidateWireTests
{
	[Theory]
	[InlineData("jev-latest")]
	[InlineData("jev-1.14")]
	[InlineData("jev-1.13.0")]
	[InlineData("")]
	[InlineData(null)]
	public void RejectsAliasesAndUnsupportedModelsBeforePreparingRequests(string? model)
	{
		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(new("Apples", null, null), "v1",
			[new SuggestionCategory("apple", "Apples", "Fresh apples", null)], ["apple"]);
		Assert.Throws<ArgumentException>(() => CandidateWire.Prepare(plan, model!));
	}
	[Fact]
	public void SerializesTypedQuestionsAndReducesRawAnswersOffline()
	{
		CandidateWireRequest request = Prepare();
		JsonObject payload = JsonNode.Parse(request.Payload)!.AsObject();
		Assert.Equal("jev-1.14.0", payload["model"]!.GetValue<string>());
		Assert.Equal("Fresh apples", payload["state"]!["purchase"]!["description"]!.GetValue<string>());
		Assert.Equal(6, payload["questions"]!.AsObject().Count);
		Assert.All(payload["questions"]!.AsObject(), question => Assert.Equal("noul", question.Value!["type"]!.GetValue<string>()));
		CandidateWireResult result = request.ParseResponse(Response(request).ToJsonString());
		Assert.Equal("apple", result.Decision.CategoryId);
		Assert.Equal(new[] { "food", "apple" }, result.Decision.Path);
		Assert.NotNull(result.Judgment);
		Assert.Equal(12, result.InputTokens);
		Assert.Equal(Prepare().Payload, request.Payload);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("extra")]
	[InlineData("wrong-type")]
	[InlineData("range")]
	[InlineData("string")]
	[InlineData("missing-noul")]
	[InlineData("extra-field")]
	[InlineData("model")]
	[InlineData("negative-usage")]
	[InlineData("missing-usage")]
	[InlineData("fractional-usage")]
	[InlineData("extra-root")]
	public void RejectsInvalidWireAnswersAsFailures(string change)
	{
		CandidateWireRequest request = Prepare();
		JsonObject response = Response(request);
		JsonObject answers = response["answers"]!.AsObject();
		string key = answers.First().Key;
		switch (change)
		{
			case "missing": answers.Remove(key); break;
			case "extra": answers["unknown"] = new JsonObject { ["type"] = "noul", ["noul"] = .9 }; break;
			case "wrong-type": answers[key]!["type"] = "choice"; break;
			case "range": answers[key]!["noul"] = 1.01; break;
			case "string": answers[key]!["noul"] = "0.9"; break;
			case "missing-noul": answers[key]!.AsObject().Remove("noul"); break;
			case "extra-field": answers[key]!["extra"] = true; break;
			case "model": response["model"] = "other"; break;
			case "negative-usage": response["usage"]!["input_tokens"] = -1; break;
			case "missing-usage": response.Remove("usage"); break;
			case "fractional-usage": response["usage"]!["output_tokens"] = 1.5; break;
			case "extra-root": response["unexpected"] = true; break;
		}
		AssertFailure(request.ParseResponse(response.ToJsonString()));
	}

	[Theory]
	[InlineData("description")]
	[InlineData("revision")]
	[InlineData("taxonomy")]
	[InlineData("shortlist")]
	[InlineData("store")]
	[InlineData("product-code")]
	public void AnswersCannotBeReplayedAgainstDifferentPlans(string change)
	{
		CandidateWireRequest original = Prepare();
		CandidateWireRequest changed = Prepare(change);
		Assert.NotEqual(original.PlanSha256, changed.PlanSha256);
		AssertFailure(changed.ParseResponse(Response(original).ToJsonString()));
	}

	[Fact]
	public void RejectsMalformedDuplicateAndOversizedResponses()
	{
		CandidateWireRequest request = Prepare();
		AssertFailure(request.ParseResponse("{"));
		AssertFailure(request.ParseResponse("null"));
		AssertFailure(request.ParseResponse(new string(' ', CandidateWire.MaximumResponseBytes + 1)));
		string raw = Response(request).ToJsonString();
		AssertFailure(request.ParseResponse(raw.Replace("\"model\":\"jev-1.14.0\"", "\"model\":\"jev-1.14.0\",\"model\":\"jev-1.14.0\"")));
		string key = Response(request)["answers"]!.AsObject().First().Key;
		AssertFailure(request.ParseResponse(raw.Replace($"\"{key}\":", $"\"{key}\":{{\"type\":\"noul\",\"noul\":0.9}},\"{key}\":")));
	}

	[Fact]
	public void ValidUncertainJudgmentsRemainAbstentionsAndDoNotAuthorizeAssignments()
	{
		CandidateWireRequest request = Prepare();
		JsonObject response = Response(request);
		string evidence = response["answers"]!.AsObject().Single(answer => answer.Key.EndsWith("_purchase", StringComparison.Ordinal)).Key;
		response["answers"]![evidence]!["noul"] = .5;
		CandidateWireResult result = request.ParseResponse(response.ToJsonString());
		Assert.Equal("uncertain-purchase-evidence", result.Decision.AbstentionReason);
		Assert.Null(result.Decision.Failure);
		Assert.DoesNotContain(typeof(CandidateWireRequest).GetInterfaces(), type => type == typeof(ICategorySuggestionProvider));
		Assert.DoesNotContain(typeof(CandidateWire).Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "Infrastructure");
	}

	[Fact]
	public void MapsEachPathContextToTheCandidateInsteadOfTreatingItAsSupport()
	{
		CandidateWireRequest request = Prepare();
		JsonObject response = Response(request);
		string appleContext = response["answers"]!.AsObject().Single(answer => answer.Key.EndsWith("_path_0_context", StringComparison.Ordinal)).Key;
		response["answers"]![appleContext]!["noul"] = .4;
		CandidateWireResult result = request.ParseResponse(response.ToJsonString());
		Assert.Equal("food", result.Decision.CategoryId);
		Assert.Equal(.4, result.Judgment!.Paths["apple"].ContextSufficiencyProbability);
		Assert.Equal(.95, result.Judgment.Paths["apple"].SupportProbability);
		Assert.Equal(.95, result.Judgment.Paths["food"].ContextSufficiencyProbability);
	}

	private static void AssertFailure(CandidateWireResult result)
	{
		Assert.Equal("invalid-response", result.Decision.Failure);
		Assert.Null(result.Decision.CategoryId);
		Assert.Null(result.Decision.AbstentionReason);
		Assert.Null(result.Judgment);
	}

	private static CandidateWireRequest Prepare(string? change = null)
	{
		SuggestionCategory[] taxonomy = [new("food", "Food", "Edible goods", null), new("apple", "Apples", change == "taxonomy" ? "Dessert apples" : "Fresh apples", "food")];
		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(new(change == "description" ? "Pears" : "Fresh apples", change == "store" ? "Synthetic store" : null, change == "product-code" ? "SYNTHETIC" : null),
			change == "revision" ? "v2" : "v1", taxonomy, change == "shortlist" ? ["food"] : ["apple"]);
		return CandidateWire.Prepare(plan, "jev-1.14.0");
	}

	private static JsonObject Response(CandidateWireRequest request)
	{
		JsonObject answers = new();
		foreach (KeyValuePair<string, JsonNode?> question in JsonNode.Parse(request.Payload)!["questions"]!.AsObject())
		{
			answers[question.Key] = new JsonObject { ["type"] = "noul", ["noul"] = question.Key.EndsWith("_unlisted", StringComparison.Ordinal) ? .1 : .95 };
		}
		return new JsonObject { ["model"] = "jev-1.14.0", ["answers"] = answers, ["usage"] = new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 6 } };
	}
}
