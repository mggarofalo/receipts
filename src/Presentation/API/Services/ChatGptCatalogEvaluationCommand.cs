using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.NormalizationAi;

namespace API.Services;

/// <summary>Bounded synthetic experiment for the catalog decision protocol, separate from database retrieval tests.</summary>
public static class ChatGptCatalogEvaluationCommand
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

	public static async Task<int> RunAsync(string model, string split, string reportPath, IChatGptRunner runner,
		TextWriter output, CancellationToken cancellationToken)
	{
		if (split is not ("example" or "holdout") || string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl))
		{ throw new ArgumentException("Choose an explicit model and split."); }
		string destination = Path.GetFullPath(reportPath);
		await using FileStream file = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
		using Stream resource = typeof(ChatGptCatalogEvaluationCommand).Assembly.GetManifestResourceStream("Receipts.NormalizationEvaluation.catalog.v2.json")
			?? throw new InvalidOperationException("Embedded catalog corpus is unavailable.");
		using StreamReader reader = new(resource, Encoding.UTF8);
		string text = await reader.ReadToEndAsync(cancellationToken);
		using JsonDocument document = JsonDocument.Parse(text);
		JsonElement corpus = document.RootElement;
		if (corpus.GetProperty("schemaVersion").GetString() != "2"
			|| corpus.GetProperty("provenance").GetString() != "wholly-synthetic"
			|| corpus.GetProperty("labelStatus").GetString() != "proposed-synthetic")
		{ throw new InvalidOperationException("Review the catalog corpus contract before evaluation."); }
		List<JsonElement> cases = corpus.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("split").GetString() == split).ToList();
		if (cases.Count is < 1 or > 100) { throw new InvalidOperationException("Invalid bounded corpus."); }
		List<CaseResult> results = cases.Select(item => new CaseResult
		{
			Id = item.GetProperty("id").GetString()!,
			Family = item.GetProperty("family").GetString()!,
			Tags = item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray(),
			ExpectedDecision = item.GetProperty("expected").GetProperty("decision").GetString()!,
			ExpectedCandidateKey = item.GetProperty("expected").GetProperty("candidateKey").GetString(),
			KnownMatchKey = item.GetProperty("knownMatchKey").GetString(),
			KnownMatchPresent = item.GetProperty("knownMatchKey").ValueKind == JsonValueKind.Null ? null
				: item.GetProperty("candidates").EnumerateArray().Any(candidate =>
					candidate.GetProperty("key").GetString() == item.GetProperty("knownMatchKey").GetString()),
		}).ToList();
		int attempted = 0;
		int exitCode = 0;
		string? errorCode = null;
		DateTimeOffset startedAt = DateTimeOffset.UtcNow;
		await output.WriteLineAsync($"Synthetic catalog {split} experiment: at most {cases.Count} model requests. Requests may consume plan allowance. No receipt database is read or changed.");
		try
		{
			ChatGptRunnerStatus status = await runner.GetStatusAsync(cancellationToken);
			if (status.Busy) { throw new ChatGptRunnerException("busy"); }
			if (!status.CredentialsPresent) { throw new ChatGptRunnerException("auth_required"); }
			if (!(await runner.GetModelsAsync(cancellationToken)).Contains(model, StringComparer.Ordinal))
			{ throw new ChatGptRunnerException("model_unavailable"); }
			for (int index = 0; index < cases.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				JsonElement scenario = cases[index];
				CaseResult result = results[index];
				// Random transport identifiers do not disclose fixture labels or expected answers.
				Dictionary<Guid, JsonElement> candidates = scenario.GetProperty("candidates").EnumerateArray().ToDictionary(_ => Guid.NewGuid());
				NormalizationAiInput[] inputs = [new(Guid.NewGuid(), scenario.GetProperty("description").GetString()!,
					candidates.Select(pair => new NormalizationAiTarget(pair.Key, Guid.NewGuid(), pair.Value.GetProperty("name").GetString()!, null, null,
						ApprovedEvidence: pair.Value.GetProperty("approvedExamples").EnumerateArray().Select(example =>
							new NormalizationApprovedExample(example.GetProperty("description").GetString()!, example.GetProperty("sameScope").GetBoolean())).ToArray())).ToArray())];
				string prompt = NormalizationCatalogAiPolicy.BuildPrompt(inputs);
				result.PromptSha256 = Digest(prompt);
				result.Status = "in_flight";
				attempted++;
				Stopwatch timer = Stopwatch.StartNew();
				try
				{
					ChatGptInferenceResult response = await runner.InferAsync(new(model, NormalizationCatalogAiPolicy.Instructions,
						prompt, NormalizationCatalogAiPolicy.Schema), cancellationToken);
					result.Usage = response.Usage;
					NormalizationAiDecision decision = NormalizationCatalogAiPolicy.Validate(response.Text, inputs).Single();
					result.Decision = decision.Action switch { "reuse" => "same", "create" => "different", _ => "insufficient" };
					result.CandidateKey = decision.TargetId.HasValue ? candidates[decision.TargetId.Value].GetProperty("key").GetString() : null;
					result.ProposedName = decision.DisplayLabel;
					result.SuggestedDisplayLabel = decision.SuggestedDisplayLabel;
					result.AgreesWithProposedLabel = result.Decision == result.ExpectedDecision && result.CandidateKey == result.ExpectedCandidateKey;
					result.Status = "validated";
				}
				catch
				{
					result.Status = "failed_or_unknown";
					throw;
				}
				finally { result.ElapsedMilliseconds = timer.ElapsedMilliseconds; }
				await output.WriteLineAsync($"Synthetic catalog case {result.Id}: {result.Status}.");
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{ errorCode = "cancelled_unknown_usage"; exitCode = 130; }
		catch (ChatGptRunnerException exception) { errorCode = SafeCode(exception.Code); exitCode = 1; }
		catch (NormalizationAiException exception) { errorCode = SafeCode(exception.Code); exitCode = 1; }
		catch (Exception) { errorCode = "evaluation_failed"; exitCode = 1; }
		var report = new
		{
			reportVersion = "catalog-2",
			provider = "openai-codex",
			model,
			split,
			promptVersion = NormalizationCatalogAiPolicy.Version,
			corpusVersion = corpus.GetProperty("corpusVersion").GetString(),
			corpusSha256 = Digest(text),
			codeVersion = typeof(ChatGptCatalogEvaluationCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
			instructionsSha256 = Digest(NormalizationCatalogAiPolicy.Instructions),
			schemaSha256 = Digest(NormalizationCatalogAiPolicy.Schema.GetRawText()),
			labelStatus = "proposed-synthetic; unadjudicated",
			namingQuality = new
			{
				status = "unadjudicated",
				meaning = "Identity agreement does not measure readability or factual naming quality. ProposedName creates a distinct identity; SuggestedDisplayLabel changes presentation of an existing identity only.",
				humanReviewRubric = new[]
				{
					"Readability improves over the existing canonical/display name without changing product identity.",
					"Known brand, package size, units, flavor, preparation and dietary distinctions are preserved.",
					"Missing attributes are not invented; ambiguous abbreviations require abstention or human clarification.",
					"Names stay concise and consistent; valid wording alternatives are not judged by exact-string equality.",
					"Global label changes require separate human review of the current product revision and affected purchases."
				}
			},
			qualityGate = "Not established: human labels and representative live baseline required.",
			retrievalEvidence = "Candidate presence in synthetic fixtures only; production retrieval recall requires separate database tests.",
			startedAt,
			completedAt = DateTimeOffset.UtcNow,
			plannedRequests = cases.Count,
			attemptedRequests = attempted,
			status = exitCode == 0 ? "completed" : exitCode == 130 ? "cancelled" : "stopped_on_failure",
			errorCode,
			metrics = Measure(results),
			cases = results,
		};
		await JsonSerializer.SerializeAsync(file, report, JsonOptions, CancellationToken.None);
		await file.FlushAsync(CancellationToken.None);
		await output.WriteLineAsync($"Report saved: {destination}. Schema validity and proposed-label agreement do not prove semantic accuracy.");
		return exitCode;
	}

	internal static object Measure(IReadOnlyList<CaseResult> cases)
	{
		CaseResult[] completed = cases.Where(item => item.Status == "validated").ToArray();
		CaseResult[] known = cases.Where(item => item.KnownMatchPresent.HasValue).ToArray();
		CaseResult[] retrieved = completed.Where(item => item.KnownMatchPresent == true && item.ExpectedDecision == "same").ToArray();
		CaseResult[] allRetrieved = completed.Where(item => item.KnownMatchPresent == true).ToArray();
		CaseResult[] predictedSame = completed.Where(item => item.Decision == "same").ToArray();
		int falseMatches = predictedSame.Count(item => item.AgreesWithProposedLabel != true);
		int attempted = cases.Count(item => item.Status != "not_attempted");
		long[] durations = cases.Where(item => item.ElapsedMilliseconds.HasValue).Select(item => item.ElapsedMilliseconds!.Value).Order().ToArray();
		return new
		{
			completed = completed.Length,
			total = cases.Count,
			failed = attempted - completed.Length,
			unattempted = cases.Count - attempted,
			fixtureCandidateRecall = Rate(known.Count(item => item.KnownMatchPresent == true), known.Length),
			provisionalDecisionAgreement = Rate(completed.Count(item => item.AgreesWithProposedLabel == true), completed.Length),
			provisionalAgreementPerAttempt = Rate(completed.Count(item => item.AgreesWithProposedLabel == true), attempted),
			provisionalMatchAccuracyGivenCandidate = Rate(retrieved.Count(item => item.AgreesWithProposedLabel == true), retrieved.Length),
			provisionalAgreementGivenKnownCandidate = Rate(allRetrieved.Count(item => item.AgreesWithProposedLabel == true), allRetrieved.Length),
			provisionalFalseMatchFraction = Rate(falseMatches, predictedSame.Length),
			provisionalFalseMatchesPerAttempt = Rate(falseMatches, attempted),
			abstention = Rate(completed.Count(item => item.Decision == "insufficient"), completed.Length),
			failedOrUnattempted = Rate(cases.Count - completed.Length, cases.Count),
			latencyMilliseconds = new
			{
				count = durations.Length,
				p50 = Percentile(durations, 0.5),
				p95 = Percentile(durations, 0.95),
				method = "nearest-rank, includes completed and failed timed requests",
			},
			pairedControls = cases.SelectMany(item => item.Tags.Where(tag => tag.StartsWith("pair:", StringComparison.Ordinal)
				|| tag.StartsWith("ablation:", StringComparison.Ordinal)).Select(tag => new { tag, item }))
				.GroupBy(value => value.tag).ToDictionary(group => group.Key,
					group => group.Select(value => new { value.item.Id, value.item.Status, value.item.Decision, value.item.CandidateKey, value.item.AgreesWithProposedLabel }).ToArray()),
		};
	}
	private static long? Percentile(long[] values, double percentile) => values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * percentile) - 1];
	private static object Rate(int numerator, int denominator) => new { numerator, denominator, rate = denominator == 0 ? (double?)null : (double)numerator / denominator };
	private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	private static string SafeCode(string code) => code.Length <= 64 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? code : "provider_failure";

	internal sealed class CaseResult
	{
		public string Id { get; init; } = "";
		public string Family { get; init; } = "";
		public string[] Tags { get; init; } = [];
		public string ExpectedDecision { get; init; } = "";
		public string? ExpectedCandidateKey { get; init; }
		public string? KnownMatchKey { get; init; }
		public bool? KnownMatchPresent { get; init; }
		public string Status { get; set; } = "not_attempted";
		public string? Decision { get; set; }
		public string? CandidateKey { get; set; }
		public string? ProposedName { get; set; }
		public string? SuggestedDisplayLabel { get; set; }
		public bool? AgreesWithProposedLabel { get; set; }
		public string? PromptSha256 { get; set; }
		public long? ElapsedMilliseconds { get; set; }
		public ChatGptUsage? Usage { get; set; }
	}
}


