using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;

namespace API.Services;

/// <summary>Explicit operator experiment over the embedded synthetic corpus, never application receipt data.</summary>
public static class ChatGptEvaluationCommand
{
	private const string ResourceName = "Receipts.NormalizationEvaluation.corpus.v1.json";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

	public static async Task<int> RunAsync(string model, string split, string reportPath, IChatGptRunner runner,
		TextWriter output, CancellationToken cancellationToken)
	{
		if (split is not ("example" or "holdout") || string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl))
		{ throw new ArgumentException("Choose an explicit model and example or holdout split."); }
		string destination = Path.GetFullPath(reportPath);
		// Reserve an operator-selected new file before any provider call. Existing reports are never replaced.
		await using FileStream file = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
		using Stream resource = typeof(ChatGptEvaluationCommand).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException("Embedded synthetic corpus is unavailable.");
		using StreamReader reader = new(resource, Encoding.UTF8);
		string corpusText = await reader.ReadToEndAsync(cancellationToken);
		using JsonDocument corpus = JsonDocument.Parse(corpusText);
		JsonElement root = corpus.RootElement;
		if (root.GetProperty("provenance").GetString() != "wholly-synthetic" || root.GetProperty("labelStatus").GetString() != "proposed-synthetic"
			|| root.GetProperty("humanReview").GetProperty("status").GetString() != "unadjudicated")
		{ throw new InvalidOperationException("Review the evaluator before changing the embedded corpus contract."); }
		List<JsonElement> cases = root.GetProperty("cases").EnumerateArray().Where(value => value.GetProperty("split").GetString() == split).ToList();
		EvaluationReport report = new()
		{
			Model = model,
			Split = split,
			CorpusVersion = root.GetProperty("corpusVersion").GetString()!,
			CorpusSha256 = Digest(corpusText),
			CodeVersion = typeof(ChatGptEvaluationCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
			InstructionsSha256 = Digest(NormalizationAiPolicy.Instructions),
			SchemaSha256 = Digest(NormalizationAiPolicy.Schema.GetRawText()),
			Cases = cases.Select(CreateCase).ToList(),
		};
		report.PlannedRequests = report.Cases.Count(value => value.Items.Any(item => item.Eligible));
		await output.WriteLineAsync($"Synthetic {split} experiment: at most {report.PlannedRequests} model requests; usage may be consumed. No receipt database is read or changed.");
		int exitCode = 0;
		try
		{
			ChatGptRunnerStatus status = await runner.GetStatusAsync(cancellationToken);
			if (status.Busy) { throw new ChatGptRunnerException("busy"); }
			if (!status.CredentialsPresent) { throw new ChatGptRunnerException("auth_required"); }
			if (!(await runner.GetModelsAsync(cancellationToken)).Contains(model, StringComparer.Ordinal))
			{ throw new ChatGptRunnerException("model_unavailable"); }
			foreach (EvaluationCase scenario in report.Cases)
			{
				if (!scenario.Items.Any(item => item.Eligible)) { scenario.Status = "protected_only"; continue; }
				if (report.ErrorCode is not null) { continue; }
				cancellationToken.ThrowIfCancellationRequested();
				Dictionary<Guid, EvaluationItem> keys = scenario.Items.Where(item => item.Eligible).ToDictionary(_ => Guid.NewGuid());
				// Candidate names come from observed source text of existing synthetic assignments, never expected labels.
				Dictionary<Guid, EvaluationItem> targets = scenario.Items.Where(item => item.CurrentGroup is not null)
					.GroupBy(item => item.CurrentGroup).Select(group => group.First()).ToDictionary(_ => Guid.NewGuid());
				NormalizationAiTarget[] candidates = targets.Select(pair => new NormalizationAiTarget(pair.Key, Guid.NewGuid(), pair.Value.Description, null, null)).ToArray();
				NormalizationAiInput[] inputs = keys.Select(pair => new NormalizationAiInput(pair.Key, pair.Value.Description, candidates)).ToArray();
				string prompt = NormalizationAiPolicy.BuildPrompt(inputs);
				scenario.PromptSha256 = Digest(prompt);
				report.AttemptedRequests++;
				scenario.Status = "in_flight";
				Stopwatch timer = Stopwatch.StartNew();
				try
				{
					ChatGptInferenceResult response = await runner.InferAsync(new(model, NormalizationAiPolicy.Instructions, prompt, NormalizationAiPolicy.Schema), cancellationToken);
					scenario.Usage = response.Usage;
					IReadOnlyList<NormalizationAiDecision> decisions = NormalizationAiPolicy.Validate(response.Text, inputs);
					scenario.SchemaValid = true;
					foreach (NormalizationAiDecision decision in decisions)
					{
						EvaluationItem item = keys[decision.Id];
						item.Outcome = decision.Action == "abstain" ? "abstain" : "suggest";
						item.Action = decision.Action;
						item.ProposedName = decision.Action == "reuse" ? targets[decision.TargetId!.Value].Description : decision.DisplayLabel;
						item.PredictedGroup = decision.Action == "reuse" ? "existing:" + targets[decision.TargetId!.Value].CurrentGroup
							: decision.Action == "create" ? "new:" + decision.DisplayLabel!.ToLowerInvariant() : null;
					}
					scenario.Status = "validated";
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					scenario.Status = "unknown"; report.ErrorCode = "cancelled_unknown_usage"; exitCode = 130;
					foreach (EvaluationItem item in keys.Values) { item.Outcome = "failed"; }
				}
				catch (NormalizationAiException exception)
				{
					scenario.Status = "invalid"; report.ErrorCode = SafeCode(exception.Code); exitCode = 1;
					foreach (EvaluationItem item in keys.Values) { item.Outcome = "failed"; }
				}
				catch (Exception exception)
				{
					scenario.Status = "unknown"; report.ErrorCode = exception is ChatGptRunnerException provider ? SafeCode(provider.Code) : "provider_failure"; exitCode = 1;
					foreach (EvaluationItem item in keys.Values) { item.Outcome = "failed"; }
				}
				finally { scenario.ElapsedMilliseconds = timer.ElapsedMilliseconds; }
				await output.WriteLineAsync($"Synthetic case {scenario.Id}: {scenario.Status}.");
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { report.ErrorCode = "cancelled"; exitCode = 130; }
		catch (Exception exception) { report.ErrorCode = exception is ChatGptRunnerException provider ? SafeCode(provider.Code) : "evaluation_failed"; exitCode = 1; }
		report.CompletedAt = DateTimeOffset.UtcNow;
		report.Status = exitCode == 0 ? "completed" : exitCode == 130 ? "cancelled" : "stopped_on_failure";
		foreach (EvaluationCase scenario in report.Cases) { scenario.Metrics = Measure([scenario]); }
		report.Metrics = Measure(report.Cases);
		// Save the partial report even on ordinary cancellation. Raw responses/reasoning/errors are never persisted.
		await JsonSerializer.SerializeAsync(file, report, JsonOptions, CancellationToken.None);
		await file.FlushAsync(CancellationToken.None);
		await output.WriteLineAsync($"Report saved: {destination}. Human quality gate remains blocked; provisional label agreement is not semantic accuracy.");
		return exitCode;
	}

	private static EvaluationCase CreateCase(JsonElement scenario)
	{
		List<EvaluationItem> items = [];
		foreach (JsonElement value in scenario.GetProperty("items").EnumerateArray())
		{
			string? group = value.GetProperty("currentGroup").GetString();
			bool rejected = value.GetProperty("exactRejected").GetBoolean();
			string description = value.GetProperty("description").GetString()!;
			bool eligible = NormalizationAiDecisionPolicy.IsEligibleSource(new(Guid.NewGuid(), description,
				group is null ? null : Guid.NewGuid(), null, group is null ? null : NormalizedDescriptionStatus.Active,
				TemplateProtected: value.GetProperty("templateGroup").ValueKind != JsonValueKind.Null, ExactRejected: rejected));
			JsonElement expected = value.GetProperty("expected");
			items.Add(new()
			{
				Id = value.GetProperty("id").GetString()!,
				Description = description,
				CurrentGroup = group,
				Eligible = eligible,
				Outcome = eligible ? "not_attempted" : group is not null ? "preserve" : "excluded_tombstone",
				PredictedGroup = group is null ? null : "existing:" + group,
				ExpectedAction = expected.GetProperty("action").GetString()!,
				ExpectedGroup = expected.GetProperty("group").GetString(),
				ExpectedDisplay = expected.GetProperty("display").GetString(),
			});
		}
		return new()
		{
			Id = scenario.GetProperty("id").GetString()!,
			Difficulty = scenario.GetProperty("difficulty").GetString()!,
			Tags = scenario.GetProperty("tags").EnumerateArray().Select(value => value.GetString()!).ToArray(),
			Items = items,
			ForbiddenPairs = scenario.GetProperty("forbiddenMerges").EnumerateArray().Select(pair => pair.EnumerateArray().Select(value => value.GetString()!).ToArray()).ToArray(),
		};
	}

	internal static EvaluationMetrics Measure(IReadOnlyList<EvaluationCase> cases)
	{
		EvaluationItem[] eligible = cases.SelectMany(value => value.Items).Where(item => item.Eligible).ToArray();
		int positive = 0, negative = 0, predictedEqual = 0, truePositive = 0, falsePositive = 0, forbidden = 0, forbiddenEqual = 0;
		foreach (EvaluationCase scenario in cases)
		{
			EvaluationItem[] grouped = scenario.Items.Where(item => item.Eligible && item.ExpectedAction == "group").ToArray();
			for (int left = 0; left < grouped.Length; left++)
			{
				for (int right = left + 1; right < grouped.Length; right++)
				{
					bool expectedSame = grouped[left].ExpectedGroup == grouped[right].ExpectedGroup;
					if (expectedSame) { positive++; } else { negative++; }
					if (!SamePrediction(grouped[left], grouped[right])) { continue; }
					predictedEqual++;
					if (expectedSame) { truePositive++; } else { falsePositive++; }
				}
			}
			foreach (string[] pair in scenario.ForbiddenPairs)
			{
				forbidden++;
				if (SamePrediction(scenario.Items.Single(item => item.Id == pair[0]), scenario.Items.Single(item => item.Id == pair[1]))) { forbiddenEqual++; }
			}
		}
		return new(eligible.Length, cases.Sum(value => value.Items.Count(item => !item.Eligible)),
			Rate(eligible.Count(item => item.Outcome == "suggest"), eligible.Length), Rate(eligible.Count(item => item.Outcome == "abstain"), eligible.Length),
			Rate(eligible.Count(item => item.Outcome is "failed" or "not_attempted"), eligible.Length),
			Rate(truePositive, predictedEqual), Rate(truePositive, positive), Rate(falsePositive, negative), Rate(forbiddenEqual, forbidden),
			Rate(eligible.Count(item => item.ExpectedAction == "abstain" && item.Outcome == "suggest"), eligible.Count(item => item.ExpectedAction == "abstain")));
	}
	private static bool SamePrediction(EvaluationItem left, EvaluationItem right)
		=> left.PredictedGroup is not null && left.PredictedGroup == right.PredictedGroup;
	private static CountRate Rate(int numerator, int denominator) => new(numerator, denominator, denominator == 0 ? null : (double)numerator / denominator,
		denominator == 0 ? "No eligible observations for this metric." : null);
	private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	private static string SafeCode(string code) => code.Length <= 64 && code.All(value => char.IsAsciiLetterOrDigit(value) || value == '_') ? code : "provider_failure";

	internal sealed class EvaluationReport
	{
		public string ReportVersion { get; } = "1";
		public string Provider { get; } = "openai-codex";
		public string ProviderRuntime { get; } = "@earendil-works/pi-coding-agent@0.99.2";
		public string PromptVersion { get; } = "receipts-normalization-v1";
		public string Model { get; init; } = "";
		public string Split { get; init; } = "";
		public string CorpusVersion { get; init; } = "";
		public string CorpusSha256 { get; init; } = "";
		public string CodeVersion { get; init; } = "";
		public string InstructionsSha256 { get; init; } = "";
		public string SchemaSha256 { get; init; } = "";
		public string LabelStatus { get; } = "proposed-synthetic; unadjudicated";
		public string QualityGate { get; } = "blocked: human adjudication, product thresholds, real baseline and representative sampling pending";
		public object? HumanSuggestionPrecision { get; } = null;
		public object? HumanLabelUsefulness { get; } = null;
		public string Baseline { get; } = "not_run; no baseline improvement claim";
		public string InvariantEvidence { get; } = "Selection exclusions only. Database apply, financial preservation, tombstone races and audit require the separate PostgreSQL integration tests; this experiment cannot certify them.";
		public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
		public DateTimeOffset? CompletedAt { get; set; }
		public string Status { get; set; } = "running";
		public string? ErrorCode { get; set; }
		public int PlannedRequests { get; set; }
		public int AttemptedRequests { get; set; }
		public List<EvaluationCase> Cases { get; init; } = [];
		public EvaluationMetrics? Metrics { get; set; }
	}
	internal sealed class EvaluationCase
	{
		public string Id { get; init; } = "";
		public string Difficulty { get; init; } = "";
		public string[] Tags { get; init; } = [];
		public string Status { get; set; } = "not_attempted";
		public bool SchemaValid { get; set; }
		public string? PromptSha256 { get; set; }
		public long? ElapsedMilliseconds { get; set; }
		public ChatGptUsage? Usage { get; set; }
		public List<EvaluationItem> Items { get; init; } = [];
		public string[][] ForbiddenPairs { get; init; } = [];
		public EvaluationMetrics? Metrics { get; set; }
	}
	internal sealed class EvaluationItem
	{
		public string Id { get; init; } = "";
		public string Description { get; init; } = "";
		public bool Eligible { get; init; }
		public string Outcome { get; set; } = "not_attempted";
		public string? Action { get; set; }
		public string? ProposedName { get; set; }
		public string? PredictedGroup { get; set; }
		public string ExpectedAction { get; init; } = "";
		public string? ExpectedGroup { get; init; }
		public string? ExpectedDisplay { get; init; }
		[JsonIgnore] public string? CurrentGroup { get; init; }
	}
	internal sealed record CountRate(int Numerator, int Denominator, double? Rate, string? NullReason);
	internal sealed record EvaluationMetrics(int EligibleItems, int ExcludedProtectedItems, CountRate Coverage, CountRate Abstention,
		CountRate FailureIncludingUnattempted, CountRate ProvisionalPairPrecision, CountRate ProvisionalPairRecall,
		CountRate ProvisionalFalseMergeRate, CountRate ProvisionalForbiddenPairRate, CountRate ProvisionalUnsupportedGuessRate);
}
