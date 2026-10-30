using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public class OfflinePairedEvaluationTests
{
	private static OfflineCase Case(string id = "synthetic-1") => new(id,
		new("synthetic apple", "synthetic-v1", [new("food", "Food", "Edible goods", null), new("fruit", "Fruit", "Fresh fruit", "food")]));

	private static string Baseline(double fit = 0.9, string child = "fruit") => JsonSerializer.Serialize(new
	{
		model = CategorySuggestionPolicy.Model,
		answers = new Dictionary<string, object>
		{
			["fits"] = new { type = "noul", noul = fit },
			["category"] = new { type = "choice", choice = "food", confidence = 0.01, probabilities = new { food = 1.0 } },
			["branch_0"] = new
			{
				type = "choice",
				choice = child,
				confidence = 0.01,
				probabilities = new Dictionary<string, double> { ["food"] = child == "food" ? 0.9 : 0.1, ["fruit"] = child == "fruit" ? 0.9 : 0.1 }
			}
		},
		usage = new { input_tokens = 100, output_tokens = 10 }
	});

	private static async Task<ReplayExchange[]> Paired(double support, string child = "fruit")
	{
		ReplayExchange baseline = new(Baseline(child: child));
		OfflineReplay missing = await OfflinePairedEvaluation.ReplayAsync([Case()], [baseline], Policies.Pilot);
		OfflineAttempt attempt = missing.Attempts[1];
		string key = JsonNode.Parse(attempt.RequestJson)!["questions"]!.AsObject().Single().Key;
		string body = JsonSerializer.Serialize(new
		{
			model = CategorySuggestionPolicy.Model,
			answers = new Dictionary<string, object> { [key] = new { type = "noul", noul = support } },
			usage = new { input_tokens = 50, output_tokens = 5 }
		});
		return [baseline, new(body, RequestSha256: attempt.RequestSha256)];
	}

	[Theory]
	[InlineData(0.699999, null)]
	[InlineData(0.7, "fruit")]
	[InlineData(0.95, "fruit")]
	public async Task ExactProductionSelectedGateRetainsSameFullPath(double support, string? expected)
	{
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case()], await Paired(support), Policies.Pilot);
		OfflineOutcome outcome = Assert.Single(result.Outcomes);
		Assert.Equal("completed", outcome.Status);
		Assert.Equal("fruit", outcome.Baseline!.Suggestion!.CategoryId);
		Assert.Equal(expected, outcome.Treatment!.Suggestion!.CategoryId);
		Assert.Equal(expected is null ? [] : new[] { "food", "fruit" }, outcome.Treatment.Suggestion.Path);
		Assert.Equal(support, outcome.Treatment.Suggestion.SelectedPathSupport);
		Assert.Equal(150, outcome.Treatment.Suggestion.Provenance.InputTokens);
		Assert.Equal(2, result.Attempts.Count);
		Assert.Equal(2 * EvaluationPolicy.PerAttemptNanodollars, result.ReservationNanodollars);
	}

	[Fact]
	public async Task SupportedParentStopIsPreserved()
	{
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case()], await Paired(0.8, "food"), Policies.Pilot);
		Assert.Equal("food", result.Outcomes[0].Treatment!.Suggestion!.CategoryId);
		Assert.Equal(["food"], result.Outcomes[0].Treatment!.Suggestion!.Path);
	}

	[Theory]
	[InlineData(0.1, "no-fit")]
	[InlineData(0.699999, "uncertain-fit")]
	public async Task ValidGlobalAbstentionSkipsSelectedCall(double fit, string reason)
	{
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case()], [new(Baseline(fit))], Policies.Pilot);
		Assert.False(result.Stopped);
		Assert.Single(result.Attempts);
		Assert.Equal(reason, result.Outcomes[0].Treatment!.Suggestion!.AbstentionReason);
		Assert.Null(result.Outcomes[0].Treatment!.Failure);
	}

	[Theory]
	[InlineData("{}", 200)]
	[InlineData("", 401)]
	[InlineData("", 429)]
	public async Task InvalidAndTransportFailuresStopWithoutReplacingSuffix(string body, int status)
	{
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case(), Case("synthetic-2")], [new(body, status)], Policies.Pilot);
		Assert.True(result.Stopped);
		Assert.Equal("failed", result.Outcomes[0].Status);
		Assert.Equal("unattempted", result.Outcomes[1].Status);
		Assert.Null(result.Outcomes[0].Baseline!.Suggestion);
		Assert.Equal(2, result.TotalCases);
		Assert.Equal(0, result.CompletedCases);
		Assert.Single(result.Attempts);
	}

	[Fact]
	public async Task MissingBoundResponseIsUnknownAndReservationSurvives()
	{
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case()], [], Policies.Pilot);
		Assert.Equal("unknown", result.Outcomes[0].Status);
		Assert.Equal(EvaluationPolicy.PerAttemptNanodollars, result.ReservationNanodollars);
		Assert.Equal(Policies.Pilot.PriorCarryNanodollars + result.ReservationNanodollars, result.CombinedReservationNanodollars);
	}

	[Fact]
	public async Task WrongSelectedQuestionBindingIsFailureNotAbstention()
	{
		ReplayExchange[] exchanges = await Paired(0.9);
		JsonNode body = JsonNode.Parse(exchanges[1].Body)!;
		JsonObject answers = body["answers"]!.AsObject();
		JsonNode value = answers.Single().Value!.DeepClone();
		answers.Clear(); answers["unbound_selected_path_support"] = value;
		exchanges[1] = exchanges[1] with { Body = body.ToJsonString() };
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync([Case()], exchanges, Policies.Pilot);
		Assert.Equal("failed", result.Outcomes[0].Status);
		Assert.Equal(SuggestionFailure.InvalidResponse, result.Outcomes[0].Treatment!.Failure);
	}

	[Fact]
	public async Task FullCohortNeverExceedsFortyEightReservations()
	{
		OfflineCase[] cases = Enumerable.Range(0, 24).Select(i => Case($"synthetic-{i}")).ToArray();
		ReplayExchange[] pair = await Paired(0.7);
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync(cases, Enumerable.Range(0, 24).SelectMany(_ => pair).ToArray(), Policies.Pilot);
		Assert.Equal(24, result.CompletedCases);
		Assert.Equal(48, result.Attempts.Count);
		Assert.Equal(132_120_576, result.ReservationNanodollars);
		Assert.Equal(4_882_120_576, result.CombinedReservationNanodollars);
		Assert.Throws<ArgumentException>(() => OfflinePairedEvaluation.ValidateCases([.. cases, Case("extra")], Policies.Pilot));
	}

	[Fact]
	public async Task FourCasePolicyCapsReplayAtEightAttempts()
	{
		OfflineCase[] cases = Enumerable.Range(0, 4).Select(i => Case($"synthetic-{i}")).ToArray();
		ReplayExchange[] pair = await Paired(0.9);
		ReplayExchange[] exchanges = Enumerable.Range(0, 4).SelectMany(_ => pair).ToArray();
		OfflineReplay result = await OfflinePairedEvaluation.ReplayAsync(cases, exchanges, Policies.Four);
		Assert.Equal(4, result.CompletedCases);
		Assert.Equal(8, result.Attempts.Count);
		Assert.Equal(0, result.PriorCarryNanodollars);
		Assert.Equal(8 * EvaluationPolicy.PerAttemptNanodollars, result.CombinedReservationNanodollars);
		await Assert.ThrowsAsync<ArgumentException>(() => OfflinePairedEvaluation.ReplayAsync(cases, [.. exchanges, pair[0]], Policies.Four));
		await Assert.ThrowsAsync<ArgumentException>(() => OfflinePairedEvaluation.ReplayAsync([.. cases, Case("extra")], exchanges, Policies.Four));
	}
}
