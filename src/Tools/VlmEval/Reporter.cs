using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace VlmEval;

public sealed class Reporter(VlmEvalOptions options, ILogger<Reporter> logger)
{
	// Indented + camelCase so the artifact is human-diffable in CI logs and obvious to consume
	// from JS/TS regression-baseline tooling. JsonStringEnumConverter keeps DiffStatus readable
	// (e.g. "Pass") rather than as integers.
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public void PrintHeader(string provider, string providerEndpoint, string fixturesPath)
	{
		logger.LogInformation("VLM accuracy eval — {Timestamp}", DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture));
		logger.LogInformation("Provider: {Provider}", provider);
		logger.LogInformation("Endpoint: {Endpoint}", providerEndpoint);
		logger.LogInformation("Fixtures directory: {Path}", fixturesPath);
	}

	public void PrintOllamaUnreachable(string endpoint)
	{
		logger.LogError("Ollama is not reachable at {Url}. Verify the vlm-ocr container is running.", endpoint);
	}

	public void PrintMissingFixturesDirectory(string path)
	{
		logger.LogError(
			"Fixtures directory does not exist: {Path}. Check VlmEval__FixturesPath — a typo'd path is treated as a hard error to avoid silently passing on no input.",
			path);
	}

	public void PrintEmptyFixturesDirectory(string path)
	{
		logger.LogWarning(
			"No fixtures found. Drop a receipt file (.jpg/.jpeg/.png/.pdf) and a <name>.<ext>.expected.json sidecar at: {Path}",
			path);
	}

	public void PrintNoValidFixtures()
	{
		logger.LogWarning("No valid fixtures (all candidate files were malformed or missing sidecars).");
	}

	public void PrintOrphan(string filePath)
	{
		logger.LogWarning(
			"Fixture {FilePath} has no companion {Sidecar}; skipping.",
			Path.GetFileName(filePath),
			Path.GetFileName(filePath) + ".expected.json");
	}

	public void PrintFixtureResult(FixtureResult result)
	{
		string status = result.Passed ? "PASS" : "FAIL";
		string elapsed = FormatElapsed(result.Elapsed);

		if (result.Error is not null)
		{
			logger.LogError("[{Status}] {Name}  {Elapsed}  ERROR: {Error}", status, result.FixtureName, elapsed, result.Error);
			return;
		}

		string summary = FormatSummary(result.FieldDiffs);
		if (result.Passed)
		{
			logger.LogInformation("[{Status}] {Name}  {Elapsed}  {Summary}", status, result.FixtureName, elapsed, summary);
		}
		else
		{
			logger.LogError("[{Status}] {Name}  {Elapsed}  {Summary}", status, result.FixtureName, elapsed, summary);
			foreach (FieldDiff diff in result.FieldDiffs.Where(d => d.Status == DiffStatus.Fail))
			{
				logger.LogError(
					"    {Field}: expected={Expected} actual={Actual}{Detail}",
					diff.Field,
					diff.Expected ?? "(none)",
					diff.Actual ?? "(none)",
					diff.Detail is null ? string.Empty : "  " + diff.Detail);
			}
		}
	}

	public void PrintSummary(IReadOnlyList<FixtureResult> results, TimeSpan totalElapsed)
	{
		int passed = results.Count(r => r.Passed);
		int failed = results.Count - passed;
		double rate = results.Count == 0 ? 0.0 : 100.0 * passed / results.Count;

		logger.LogInformation(
			"Summary: {Passed}/{Total} fixtures passed ({Rate:F0}%)  elapsed={Elapsed}",
			passed,
			results.Count,
			rate,
			FormatElapsed(totalElapsed));

		if (failed > 0)
		{
			logger.LogError(
				"Failed: {Names}",
				string.Join(", ", results.Where(r => !r.Passed).Select(r => r.FixtureName)));
		}
	}

	public void PrintCancelled(int processed, int total)
	{
		logger.LogWarning(
			"Eval cancelled — {Processed} of {Total} fixtures evaluated.",
			processed,
			total);
	}

	/// <summary>
	/// Writes a structured machine-readable report to <see cref="VlmEvalOptions.ReportPath"/> if
	/// configured. Honors <see cref="VlmEvalOptions.OutputFormat"/>: <c>Console</c> is a no-op
	/// (only loggers run), <c>Json</c> emits the canonical artifact, <c>Markdown</c> emits a
	/// human-diffable scorecard. Idempotent on empty results — used by EvalRunner to flush a
	/// report even on early-exit paths so CI always finds an artifact at the configured path.
	/// </summary>
	public void WriteReport(RunInfo run, IReadOnlyList<FixtureResult> results, TimeSpan totalElapsed, bool cancelled)
	{
		if (options.OutputFormat == ReportOutputFormat.Console)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(options.ReportPath))
		{
			logger.LogWarning(
				"OutputFormat={Format} but ReportPath is not set; skipping artifact write.",
				options.OutputFormat);
			return;
		}

		try
		{
			string? directory = Path.GetDirectoryName(options.ReportPath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			string content = options.OutputFormat switch
			{
				ReportOutputFormat.Json => RenderJson(run, results, totalElapsed, cancelled),
				ReportOutputFormat.Markdown => RenderMarkdown(run, results, totalElapsed, cancelled),
				_ => string.Empty,
			};

			File.WriteAllText(options.ReportPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			logger.LogInformation("Report written: {Path}", options.ReportPath);
		}
		catch (Exception ex)
		{
			// Failing to write the artifact must not crash the eval loop; logging it is
			// sufficient because the exit code already reflects the eval outcome.
			logger.LogError(ex, "Failed to write report to {Path}", options.ReportPath);
		}
	}

	internal static string RenderJson(RunInfo run, IReadOnlyList<FixtureResult> results, TimeSpan totalElapsed, bool cancelled)
	{
		ReportArtifact artifact = BuildArtifact(run, results, totalElapsed, cancelled);
		return JsonSerializer.Serialize(artifact, JsonOptions);
	}

	internal static string RenderMarkdown(RunInfo run, IReadOnlyList<FixtureResult> results, TimeSpan totalElapsed, bool cancelled)
	{
		StringBuilder sb = new();
		sb.AppendLine("# VLM eval report");
		sb.AppendLine();
		sb.Append("- Started: `").Append(run.StartedAt.ToString("u", CultureInfo.InvariantCulture)).AppendLine("`");
		sb.Append("- Provider: `").Append(run.Provider).AppendLine("`");
		sb.Append("- Endpoint: `").Append(run.ProviderEndpoint).AppendLine("`");
		sb.Append("- Fixtures path: `").Append(run.FixturesPath).AppendLine("`");
		sb.Append("- Cancelled: ").AppendLine(cancelled ? "yes" : "no");
		sb.AppendLine();

		int passed = results.Count(r => r.Passed);
		int failed = results.Count - passed;
		sb.AppendLine("## Summary");
		sb.AppendLine();
		sb.Append("- Total: ").AppendLine(results.Count.ToString(CultureInfo.InvariantCulture));
		sb.Append("- Passed: ").AppendLine(passed.ToString(CultureInfo.InvariantCulture));
		sb.Append("- Failed: ").AppendLine(failed.ToString(CultureInfo.InvariantCulture));
		sb.Append("- Elapsed: ").AppendLine(FormatElapsed(totalElapsed));
		sb.AppendLine();

		sb.AppendLine("## Fixtures");
		sb.AppendLine();
		if (results.Count == 0)
		{
			sb.AppendLine("_No fixtures evaluated._");
			return sb.ToString();
		}

		sb.AppendLine("| Fixture | Result | Elapsed | Notes |");
		sb.AppendLine("|---------|--------|---------|-------|");
		foreach (FixtureResult result in results)
		{
			string status = result.Passed ? "PASS" : "FAIL";
			string note;
			if (result.Error is not null)
			{
				note = $"ERROR: {EscapeMarkdownCell(result.Error)}";
			}
			else
			{
				IEnumerable<FieldDiff> failures = result.FieldDiffs.Where(d => d.Status == DiffStatus.Fail);
				note = string.Join("; ", failures.Select(f => EscapeMarkdownCell($"{f.Field}: {f.Detail ?? "fail"}")));
				if (string.IsNullOrEmpty(note))
				{
					note = "—";
				}
			}
			sb.Append("| ").Append(EscapeMarkdownCell(result.FixtureName))
				.Append(" | ").Append(status)
				.Append(" | ").Append(FormatElapsed(result.Elapsed))
				.Append(" | ").Append(note)
				.AppendLine(" |");
		}
		return sb.ToString();
	}

	private static ReportArtifact BuildArtifact(RunInfo run, IReadOnlyList<FixtureResult> results, TimeSpan totalElapsed, bool cancelled)
	{
		int passed = results.Count(r => r.Passed);
		int failed = results.Count - passed;
		List<FixtureReport> fixtures = [.. results.Select(r => new FixtureReport(
			r.FixtureName,
			r.Passed,
			(long)r.Elapsed.TotalMilliseconds,
			r.Error,
			[.. r.FieldDiffs.Select(d => new FieldDiffReport(d.Field, d.Status, d.Expected, d.Actual, d.Detail))]))];

		return new ReportArtifact(
			new RunReport(run.StartedAt, run.Provider, run.ProviderEndpoint, run.FixturesPath, cancelled),
			fixtures,
			new SummaryReport(results.Count, passed, failed, (long)totalElapsed.TotalMilliseconds));
	}

	private static string EscapeMarkdownCell(string value)
	{
		// Pipes break Markdown table cells; newlines collapse the row. Escape both.
		return value.Replace("|", "\\|", StringComparison.Ordinal)
			.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace("\n", " ", StringComparison.Ordinal);
	}

	private static string FormatElapsed(TimeSpan elapsed)
	{
		return elapsed.TotalSeconds < 60
			? $"{elapsed.TotalSeconds:F1}s"
			: $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s";
	}

	private static string FormatSummary(IReadOnlyList<FieldDiff> diffs)
	{
		List<string> parts = [];
		foreach (FieldDiff d in diffs)
		{
			string tag = d.Status switch
			{
				DiffStatus.Pass => "ok",
				DiffStatus.Fail => "FAIL",
				_ => null!,
			};
			if (tag is null)
			{
				continue;
			}

			parts.Add($"{d.Field}:{tag}");
		}
		return string.Join(" ", parts);
	}

	// Internal serialization shapes — kept private records so the public Reporter API stays
	// decoupled from the on-disk format (we can rev the artifact without breaking callers).
	internal sealed record ReportArtifact(
		[property: JsonPropertyName("run")] RunReport Run,
		[property: JsonPropertyName("fixtures")] IReadOnlyList<FixtureReport> Fixtures,
		[property: JsonPropertyName("summary")] SummaryReport Summary);

	internal sealed record RunReport(
		[property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
		[property: JsonPropertyName("provider")] string Provider,
		[property: JsonPropertyName("providerEndpoint")] string ProviderEndpoint,
		[property: JsonPropertyName("fixturesPath")] string FixturesPath,
		[property: JsonPropertyName("cancelled")] bool Cancelled)
	{
		// Backwards-compatibility shim: keep the legacy `ollamaUrl` JSON property in the
		// artifact (mirrors RunInfo.OllamaUrl). External diff scripts still parsing the old
		// shape will continue to work; new consumers should prefer `providerEndpoint`.
		[JsonPropertyName("ollamaUrl")]
		public string OllamaUrl => ProviderEndpoint;
	}

	internal sealed record FixtureReport(
		[property: JsonPropertyName("name")] string Name,
		[property: JsonPropertyName("passed")] bool Passed,
		[property: JsonPropertyName("elapsedMs")] long ElapsedMs,
		[property: JsonPropertyName("error")] string? Error,
		[property: JsonPropertyName("diffs")] IReadOnlyList<FieldDiffReport> Diffs);

	internal sealed record FieldDiffReport(
		[property: JsonPropertyName("field")] string Field,
		[property: JsonPropertyName("status")] DiffStatus Status,
		[property: JsonPropertyName("expected")] string? Expected,
		[property: JsonPropertyName("actual")] string? Actual,
		[property: JsonPropertyName("detail")] string? Detail);

	internal sealed record SummaryReport(
		[property: JsonPropertyName("total")] int Total,
		[property: JsonPropertyName("passed")] int Passed,
		[property: JsonPropertyName("failed")] int Failed,
		[property: JsonPropertyName("elapsedMs")] long ElapsedMs);
}
