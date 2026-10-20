using System.Text.Json;
using API.Services;
using Application.NormalizationAi;
using FluentAssertions;
using Moq;

namespace Presentation.API.Tests.Services;

public sealed class ChatGptEvaluationCommandTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "receipts-evaluation-tests-" + Guid.NewGuid().ToString("N"));
	private readonly Mock<IChatGptRunner> runner = new(MockBehavior.Strict);
	private readonly StringWriter output = new();
	private string ReportPath => Path.Combine(directory, "report.json");

	public ChatGptEvaluationCommandTests()
	{
		Directory.CreateDirectory(directory);
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "test-model" });
	}

	[Fact]
	public async Task ExistingReport_IsNotOverwrittenAndNoProviderOperationStarts()
	{
		await File.WriteAllTextAsync(ReportPath, "original experiment");
		Func<Task> run = () => Evaluate();
		await run.Should().ThrowAsync<IOException>();
		(await File.ReadAllTextAsync(ReportPath)).Should().Be("original experiment");
		runner.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("private-receipts")]
	[InlineData("EXAMPLE")]
	public async Task UnknownSplit_IsRejectedWithoutReadingProviderOrCreatingReport(string split)
	{
		Func<Task> run = () => Evaluate(split);
		await run.Should().ThrowAsync<ArgumentException>();
		File.Exists(ReportPath).Should().BeFalse();
		runner.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("example", 8)]
	[InlineData("holdout", 24)]
	public async Task SyntheticExperiment_DoesNotLeakExpectedLabelsOrFinancialContextAndMakesNoQualityClaim(string split, int caseCount)
	{
		List<Guid> opaqueIds = [];
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
		{
			request.Model.Should().Be("test-model");
			using var prompt = JsonDocument.Parse(request.Prompt);
			foreach (var row in prompt.RootElement.EnumerateArray())
			{
				row.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("id", "description", "candidates");
				opaqueIds.Add(row.GetProperty("id").GetGuid());
				foreach (var candidate in row.GetProperty("candidates").EnumerateArray())
				{
					candidate.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("id", "name", "displayLabel");
				}
			}
			return Abstain(request);
		});
		(await Evaluate(split)).Should().Be(0);
		using var report = JsonDocument.Parse(await File.ReadAllTextAsync(ReportPath));
		var root = report.RootElement;
		root.GetProperty("cases").GetArrayLength().Should().Be(caseCount);
		root.GetProperty("attemptedRequests").GetInt32().Should().BeInRange(1, caseCount);
		root.GetProperty("plannedRequests").GetInt32().Should().Be(root.GetProperty("attemptedRequests").GetInt32());
		root.GetProperty("qualityGate").GetString().Should().StartWith("blocked:");
		root.GetProperty("humanSuggestionPrecision").ValueKind.Should().Be(JsonValueKind.Null);
		root.GetProperty("humanLabelUsefulness").ValueKind.Should().Be(JsonValueKind.Null);
		root.GetProperty("baseline").GetString().Should().StartWith("not_run");
		root.GetProperty("labelStatus").GetString().Should().Contain("unadjudicated");
		var items = root.GetProperty("cases").EnumerateArray().SelectMany(x => x.GetProperty("items").EnumerateArray()).ToArray();
		items.Where(x => x.GetProperty("eligible").GetBoolean()).Should().OnlyContain(x => x.GetProperty("outcome").GetString() == "abstain");
		opaqueIds.Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
		opaqueIds.Count.Should().Be(items.Count(x => x.GetProperty("eligible").GetBoolean()));
		if (split == "holdout")
		{
			items.Should().Contain(x => !x.GetProperty("eligible").GetBoolean());
		}

		items.Select(x => x.GetProperty("id").GetString()).Should().NotContain(opaqueIds.Select(x => x.ToString()));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ProviderOrSchemaFailure_StopsWithoutRetryAndCountsUnattemptedRows(bool invalidOutput)
	{
		var inference = runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()));
		if (invalidOutput)
		{
			inference.ReturnsAsync(new ChatGptInferenceResult("private-invalid-provider-output"));
		}
		else
		{
			inference.ThrowsAsync(new IOException("private-provider-secret"));
		}

		(await Evaluate()).Should().Be(1);
		string text = await File.ReadAllTextAsync(ReportPath);
		text.Should().NotContain("private-invalid-provider-output").And.NotContain("private-provider-secret");
		using var report = JsonDocument.Parse(text);
		report.RootElement.GetProperty("attemptedRequests").GetInt32().Should().Be(1);
		var metrics = report.RootElement.GetProperty("metrics");
		metrics.GetProperty("failureIncludingUnattempted").GetProperty("numerator").GetInt32().Should().Be(metrics.GetProperty("eligibleItems").GetInt32());
		report.RootElement.GetProperty("cases").EnumerateArray().Should().Contain(x => x.GetProperty("status").GetString() == "not_attempted");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task InFlightCancellation_PersistsPartialReportWithoutAutomaticReplay()
	{
		using var cancellation = new CancellationTokenSource();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns((ChatGptInferenceRequest _, CancellationToken token) => { cancellation.Cancel(); return Task.FromCanceled<ChatGptInferenceResult>(token); });
		(await ChatGptEvaluationCommand.RunAsync("test-model", "example", ReportPath, runner.Object, output, cancellation.Token)).Should().Be(130);
		using var report = JsonDocument.Parse(await File.ReadAllTextAsync(ReportPath));
		report.RootElement.GetProperty("status").GetString().Should().Be("cancelled");
		report.RootElement.GetProperty("attemptedRequests").GetInt32().Should().Be(1);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public void MetricDenominators_SeparatePairsProtectedItemsAndUnattemptedAbstentions()
	{
		var scenario = new ChatGptEvaluationCommand.EvaluationCase
		{
			Items = [
			new() { Id = "a", Eligible = true, ExpectedAction = "group", ExpectedGroup = "milk", PredictedGroup = "x", Outcome = "suggest" },
			new() { Id = "b", Eligible = true, ExpectedAction = "group", ExpectedGroup = "milk", PredictedGroup = "x", Outcome = "suggest" },
			new() { Id = "c", Eligible = true, ExpectedAction = "group", ExpectedGroup = "bread", PredictedGroup = "x", Outcome = "suggest" },
			new() { Id = "d", Eligible = true, ExpectedAction = "abstain", Outcome = "not_attempted" },
			new() { Id = "protected", Eligible = false, ExpectedAction = "group", ExpectedGroup = "milk", PredictedGroup = "x", Outcome = "preserve" },
		],
			ForbiddenPairs = [["a", "c"]]
		};
		var metric = ChatGptEvaluationCommand.Measure([scenario]);
		metric.EligibleItems.Should().Be(4); metric.ExcludedProtectedItems.Should().Be(1);
		metric.Coverage.Should().Be(new ChatGptEvaluationCommand.CountRate(3, 4, .75, null));
		metric.FailureIncludingUnattempted.Should().Be(new ChatGptEvaluationCommand.CountRate(1, 4, .25, null));
		metric.ProvisionalPairPrecision.Numerator.Should().Be(1); metric.ProvisionalPairPrecision.Denominator.Should().Be(3);
		metric.ProvisionalPairRecall.Rate.Should().Be(1);
		metric.ProvisionalFalseMergeRate.Should().Be(new ChatGptEvaluationCommand.CountRate(2, 2, 1, null));
		metric.ProvisionalForbiddenPairRate.Should().Be(new ChatGptEvaluationCommand.CountRate(1, 1, 1, null));
		metric.ProvisionalUnsupportedGuessRate.Should().Be(new ChatGptEvaluationCommand.CountRate(0, 1, 0, null));
		ChatGptEvaluationCommand.Measure([]).ProvisionalPairPrecision.Rate.Should().BeNull();
	}

	private Task<int> Evaluate(string split = "example") => ChatGptEvaluationCommand.RunAsync("test-model", split, ReportPath, runner.Object, output, CancellationToken.None);
	private static ChatGptInferenceResult Abstain(ChatGptInferenceRequest request)
	{
		using var prompt = JsonDocument.Parse(request.Prompt);
		return new(JsonSerializer.Serialize(new { results = prompt.RootElement.EnumerateArray().Select(row => new { id = row.GetProperty("id").GetGuid(), action = "abstain", targetId = (Guid?)null, displayLabel = (string?)null, reason = "No confidence asserted by synthetic fake" }) }));
	}
	public void Dispose() { output.Dispose(); Directory.Delete(directory, true); }
}
