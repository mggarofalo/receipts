using System.Text.Json;
using API.Services;
using Application.NormalizationAi;
using FluentAssertions;
using Moq;

namespace Presentation.API.Tests.Services;

public sealed class ChatGptCatalogEvaluationCommandTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "catalog-evaluation-" + Guid.NewGuid().ToString("N"));
	private readonly Mock<IChatGptRunner> runner = new(MockBehavior.Strict);
	private string ReportPath => Path.Combine(directory, "report.json");
	public ChatGptCatalogEvaluationCommandTests()
	{
		Directory.CreateDirectory(directory);
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "explicit-model" });
	}

	[Theory]
	[InlineData("example", 6)]
	[InlineData("holdout", 18)]
	public async Task Evaluation_UsesOnlyObservableEvidenceFreshOpaqueIdsAndBoundedCalls(string split, int count)
	{
		List<Guid> ids = [];
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				using var document = JsonDocument.Parse(request.Prompt);
				var row = document.RootElement.EnumerateArray().Single();
				row.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("id", "description", "candidates", "labelOnly");
				Guid id = row.GetProperty("id").GetGuid();
				ids.Add(id);
				foreach (var candidate in row.GetProperty("candidates").EnumerateArray())
				{
					candidate.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("id", "name", "displayLabel", "approvedExamples");
					ids.Add(candidate.GetProperty("id").GetGuid());
					foreach (var example in candidate.GetProperty("approvedExamples").EnumerateArray())
					{
						example.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("description", "scopeRelation");
						example.GetProperty("scopeRelation").GetString().Should().BeOneOf("same-store-and-code", "other-store-or-code");
					}
				}
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new { results = new[] { new { id, identityDecision = "insufficient", candidateId = (Guid?)null, newName = (string?)null, reason = "Insufficient observable evidence." } } }));
			});
		(await Run(split)).Should().Be(0);
		ids.Should().OnlyHaveUniqueItems();
		using var report = JsonDocument.Parse(await File.ReadAllTextAsync(ReportPath));
		report.RootElement.GetProperty("plannedRequests").GetInt32().Should().Be(count);
		report.RootElement.GetProperty("attemptedRequests").GetInt32().Should().Be(count);
		report.RootElement.GetProperty("qualityGate").GetString().Should().Contain("Not established");
		report.RootElement.GetProperty("retrievalEvidence").GetString().Should().Contain("synthetic fixtures only");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(count));
	}

	[Fact]
	public async Task NamingSuggestions_AreRecordedSeparatelyFromIdentityAgreementAndRemainUnadjudicated()
	{
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				using var json = JsonDocument.Parse(request.Prompt);
				var row = json.RootElement.EnumerateArray().Single();
				var candidate = row.GetProperty("candidates").EnumerateArray().FirstOrDefault();
				bool hasCandidate = candidate.ValueKind == JsonValueKind.Object;
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new
				{
					results = new[] {
					new { id = row.GetProperty("id").GetGuid(), identityDecision = hasCandidate ? "same" : "insufficient",
						candidateId = hasCandidate ? candidate.GetProperty("id").GetGuid() : (Guid?)null,
						newName = (string?)null, suggestedDisplayLabel = hasCandidate ? "Synthetic readable label" : null,
						reason = "Synthetic fixture" } }
				}));
			});
		(await Run()).Should().Be(0);
		using var report = JsonDocument.Parse(await File.ReadAllTextAsync(ReportPath));
		var first = report.RootElement.GetProperty("cases")[0];
		first.GetProperty("suggestedDisplayLabel").GetString().Should().Be("Synthetic readable label");
		first.GetProperty("proposedName").ValueKind.Should().Be(JsonValueKind.Null);
		first.GetProperty("agreesWithProposedLabel").GetBoolean().Should().BeTrue(
			"the first fixture's correct identity remains correct independently of unadjudicated wording");
		report.RootElement.GetProperty("namingQuality").GetProperty("status").GetString().Should().Be("unadjudicated");
	}

	[Fact]
	public async Task ExistingReport_RejectsBeforeAnyProviderCall()
	{
		await File.WriteAllTextAsync(ReportPath, "existing result");
		Func<Task> run = () => Run();
		await run.Should().ThrowAsync<IOException>();
		(await File.ReadAllTextAsync(ReportPath)).Should().Be("existing result");
		runner.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ProviderOrSchemaFailure_PersistsFailureAndUnattemptedCasesWithoutLeakingOrRetrying(bool malformed)
	{
		var setup = runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()));
		if (malformed)
		{
			setup.ReturnsAsync(new ChatGptInferenceResult("private-invalid-output"));
		}
		else
		{
			setup.ThrowsAsync(new IOException("private-provider-secret"));
		}

		(await Run()).Should().Be(1);
		string text = await File.ReadAllTextAsync(ReportPath);
		text.Should().NotContain("private-invalid-output").And.NotContain("private-provider-secret");
		using var report = JsonDocument.Parse(text);
		var cases = report.RootElement.GetProperty("cases").EnumerateArray().ToArray();
		cases.Count(x => x.GetProperty("status").GetString() == "failed_or_unknown").Should().Be(1);
		cases.Count(x => x.GetProperty("status").GetString() == "not_attempted").Should().Be(5);
		var failures = report.RootElement.GetProperty("metrics").GetProperty("failedOrUnattempted");
		failures.GetProperty("numerator").GetInt32().Should().Be(6);
		failures.GetProperty("denominator").GetInt32().Should().Be(6);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Cancellation_PreservesUnknownUsageWithoutAutomaticReplay()
	{
		using var cancellation = new CancellationTokenSource();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns((ChatGptInferenceRequest _, CancellationToken token) => { cancellation.Cancel(); return Task.FromCanceled<ChatGptInferenceResult>(token); });
		(await ChatGptCatalogEvaluationCommand.RunAsync("explicit-model", "example", ReportPath, runner.Object, TextWriter.Null, cancellation.Token)).Should().Be(130);
		using var report = JsonDocument.Parse(await File.ReadAllTextAsync(ReportPath));
		report.RootElement.GetProperty("errorCode").GetString().Should().Be("cancelled_unknown_usage");
		report.RootElement.GetProperty("attemptedRequests").GetInt32().Should().Be(1);
	}

	[Fact]
	public void Metrics_SeparateMissingCandidatesWrongDecisionsFailuresAndUnattemptedCases()
	{
		ChatGptCatalogEvaluationCommand.CaseResult[] cases = [
			new() { Status = "validated", KnownMatchPresent = true, ExpectedDecision = "same", Decision = "same", AgreesWithProposedLabel = true },
			new() { Status = "validated", KnownMatchPresent = true, ExpectedDecision = "same", Decision = "same", AgreesWithProposedLabel = false },
			new() { Status = "validated", KnownMatchPresent = false, ExpectedDecision = "insufficient", Decision = "insufficient", AgreesWithProposedLabel = true },
			new() { Status = "failed_or_unknown", KnownMatchPresent = true, ExpectedDecision = "same" },
			new() { Status = "not_attempted", KnownMatchPresent = null, ExpectedDecision = "different" },
		];
		using var json = JsonDocument.Parse(JsonSerializer.Serialize(ChatGptCatalogEvaluationCommand.Measure(cases)));
		var metrics = json.RootElement;
		metrics.GetProperty("failed").GetInt32().Should().Be(1);
		metrics.GetProperty("unattempted").GetInt32().Should().Be(1);
		AssertRate(metrics, "fixtureCandidateRecall", 3, 4);
		AssertRate(metrics, "provisionalMatchAccuracyGivenCandidate", 1, 2);
		AssertRate(metrics, "provisionalAgreementPerAttempt", 2, 4);
		AssertRate(metrics, "provisionalFalseMatchFraction", 1, 2);
		AssertRate(metrics, "failedOrUnattempted", 2, 5);
	}
	private static void AssertRate(JsonElement metrics, string key, int numerator, int denominator)
	{
		metrics.GetProperty(key).GetProperty("numerator").GetInt32().Should().Be(numerator);
		metrics.GetProperty(key).GetProperty("denominator").GetInt32().Should().Be(denominator);
	}
	private Task<int> Run(string split = "example") => ChatGptCatalogEvaluationCommand.RunAsync("explicit-model", split, ReportPath, runner.Object, TextWriter.Null, CancellationToken.None);
	public void Dispose() => Directory.Delete(directory, recursive: true);
}
