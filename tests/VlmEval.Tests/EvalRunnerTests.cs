using System.Net;
using System.Net.Http;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ocr;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace VlmEval.Tests;

/// <summary>
/// Tests covering <see cref="EvalRunner"/> exit codes and the cancellation /
/// missing-fixtures-directory semantics introduced in RECEIPTS-634.
///
/// <para>
/// These tests fake just enough HTTP and extraction-service plumbing to drive the runner
/// without spinning up Aspire. The reporter is a real instance writing to a temp directory so
/// the structured-artifact behavior is exercised end-to-end.
/// </para>
/// </summary>
public class EvalRunnerTests : IDisposable
{
	private readonly string _tempDir;

	public EvalRunnerTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "vlmeval-runner-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}
		catch
		{
			// Best-effort cleanup.
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task RunAsync_MissingFixturesDirectory_FailFlagOn_ReturnsOne()
	{
		// RECEIPTS-634: a missing fixtures directory must NOT be silently created and reported
		// as success. With FailOnAnyFixtureFailure=true (the default), it returns exit 1.
		string missing = Path.Combine(_tempDir, "does-not-exist");
		EvalRunner runner = BuildRunner(
			fixturesDir: missing,
			ollamaReachable: true,
			extractionResult: null,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true });

		int exit = await runner.RunAsync(missing, CancellationToken.None);

		exit.Should().Be(1);
		Directory.Exists(missing).Should().BeFalse(because: "the runner must not auto-create the directory");
	}

	[Fact]
	public async Task RunAsync_MissingFixturesDirectory_FailFlagOff_ReturnsZero()
	{
		// With FailOnAnyFixtureFailure=false the contract is "always green when the run
		// completes without infra error". Missing directory still doesn't get auto-created
		// silently — but the exit code is 0 to honor the flag.
		string missing = Path.Combine(_tempDir, "still-missing");
		EvalRunner runner = BuildRunner(
			fixturesDir: missing,
			ollamaReachable: true,
			extractionResult: null,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = false });

		int exit = await runner.RunAsync(missing, CancellationToken.None);

		exit.Should().Be(0);
		Directory.Exists(missing).Should().BeFalse();
	}

	[Fact]
	public async Task RunAsync_EmptyFixturesDirectory_FailFlagOn_ReturnsOne()
	{
		// Directory exists but contains no fixture files. With the strict flag, this is also
		// a hard error (covers the "I committed an empty fixtures dir to CI" case).
		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: true,
			extractionResult: null,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true });

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_OllamaUnreachable_AlwaysReturnsOne()
	{
		// Infra error: Ollama unreachable always returns 1, regardless of the fail flag.
		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: false,
			extractionResult: null,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = false });

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_CancellationMidRun_ReturnsExitCode130()
	{
		// RECEIPTS-634: Ctrl+C halfway must return SIGINT exit code, not silent success.
		// Drop two valid fixtures; cancel the token after the first call begins.
		WriteFixture("a.jpg", new ExpectedReceipt { Store = "Walmart" });
		WriteFixture("b.jpg", new ExpectedReceipt { Store = "Walmart" });

		using CancellationTokenSource cts = new();
		ParsedReceipt parsed = MakeParsed("Walmart");

		Mock<IReceiptExtractionService> service = new();
		service
			.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(() =>
			{
				// Cancel after the first fixture has been processed so the loop's check at top
				// of the next iteration trips and breaks out of the loop.
				cts.Cancel();
				return parsed;
			});

		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: true,
			extractionResult: parsed,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true },
			extractionServiceOverride: service.Object);

		int exit = await runner.RunAsync(_tempDir, cts.Token);

		exit.Should().Be(130, because: "RECEIPTS-634: cancellation must propagate as POSIX SIGINT (130)");
	}

	[Fact]
	public async Task RunAsync_CancellationDuringLastFixture_ReturnsExitCode130()
	{
		// RECEIPTS-634 (find-bugs follow-up): EvaluateAsync's broad catch blocks swallow
		// OperationCanceledException from File.ReadAllBytesAsync / ExtractAsync and return a
		// normal failure result. If cancellation fires during the LAST fixture, the foreach
		// loop exits without the top-of-iteration guard tripping, and (without the post-Add
		// cancellation check) we'd return 0 or 1 instead of 130. Pin the corrected behavior.
		WriteFixture("only.jpg", new ExpectedReceipt { Store = "Walmart" });

		using CancellationTokenSource cts = new();

		Mock<IReceiptExtractionService> service = new();
		service
			.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
			.Returns<byte[], CancellationToken>((_, _) =>
			{
				// Cancel during the call so the OCE inside EvaluateAsync is swallowed by its
				// catch (Exception) and turned into a normal failure result. The runner must
				// still detect cancellation post-iteration on what is the final fixture.
				cts.Cancel();
				throw new OperationCanceledException(cts.Token);
			});

		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: true,
			extractionResult: null,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true },
			extractionServiceOverride: service.Object);

		int exit = await runner.RunAsync(_tempDir, cts.Token);

		exit.Should().Be(130, because: "cancellation absorbed by EvaluateAsync's catch on the last fixture must still surface as SIGINT");
	}

	[Fact]
	public async Task RunAsync_AllFixturesPass_ReturnsZero()
	{
		WriteFixture("ok.jpg", new ExpectedReceipt { Store = "Walmart" });
		ParsedReceipt parsed = MakeParsed("Walmart Supercenter");
		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: true,
			extractionResult: parsed,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true });

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(0);
	}

	[Fact]
	public async Task RunAsync_OneFixtureFails_FailFlagOn_ReturnsOne()
	{
		WriteFixture("fail.jpg", new ExpectedReceipt { Store = "Walmart" });
		ParsedReceipt parsed = MakeParsed("Target"); // mismatch
		EvalRunner runner = BuildRunner(
			fixturesDir: _tempDir,
			ollamaReachable: true,
			extractionResult: parsed,
			options: new VlmEvalOptions { FailOnAnyFixtureFailure = true });

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_JsonReport_WritesStructuredArtifact()
	{
		WriteFixture("ok.jpg", new ExpectedReceipt { Store = "Walmart" });
		ParsedReceipt parsed = MakeParsed("Walmart Supercenter");
		string reportPath = Path.Combine(_tempDir, "report.json");
		VlmEvalOptions options = new()
		{
			FailOnAnyFixtureFailure = true,
			OutputFormat = ReportOutputFormat.Json,
			ReportPath = reportPath,
		};
		EvalRunner runner = BuildRunner(_tempDir, ollamaReachable: true, extractionResult: parsed, options);

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(0);
		File.Exists(reportPath).Should().BeTrue();

		string json = File.ReadAllText(reportPath);
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		root.GetProperty("run").GetProperty("ollamaUrl").GetString().Should().NotBeNullOrEmpty();
		root.GetProperty("run").GetProperty("cancelled").GetBoolean().Should().BeFalse();
		root.GetProperty("fixtures").GetArrayLength().Should().Be(1);
		JsonElement fixture0 = root.GetProperty("fixtures")[0];
		fixture0.GetProperty("name").GetString().Should().Be("ok.jpg");
		fixture0.GetProperty("passed").GetBoolean().Should().BeTrue();
		fixture0.GetProperty("elapsedMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
		fixture0.GetProperty("diffs").GetArrayLength().Should().BeGreaterThan(0);
		root.GetProperty("summary").GetProperty("total").GetInt32().Should().Be(1);
		root.GetProperty("summary").GetProperty("passed").GetInt32().Should().Be(1);
		root.GetProperty("summary").GetProperty("failed").GetInt32().Should().Be(0);
	}

	[Fact]
	public async Task RunAsync_MarkdownReport_WritesHumanReadableArtifact()
	{
		WriteFixture("ok.jpg", new ExpectedReceipt { Store = "Walmart" });
		ParsedReceipt parsed = MakeParsed("Walmart Supercenter");
		string reportPath = Path.Combine(_tempDir, "report.md");
		VlmEvalOptions options = new()
		{
			FailOnAnyFixtureFailure = true,
			OutputFormat = ReportOutputFormat.Markdown,
			ReportPath = reportPath,
		};
		EvalRunner runner = BuildRunner(_tempDir, ollamaReachable: true, extractionResult: parsed, options);

		int exit = await runner.RunAsync(_tempDir, CancellationToken.None);

		exit.Should().Be(0);
		string md = File.ReadAllText(reportPath);
		md.Should().Contain("# VLM eval report");
		md.Should().Contain("## Summary");
		md.Should().Contain("## Fixtures");
		md.Should().Contain("ok.jpg");
		md.Should().Contain("PASS");
	}

	[Fact]
	public async Task RunAsync_JsonReport_OnMissingDirectory_StillEmitsArtifact()
	{
		// Even on early-exit paths (missing dir, infra error) the runner must flush a report so
		// a CI consumer always finds a parseable artifact at the configured path.
		string missing = Path.Combine(_tempDir, "missing");
		string reportPath = Path.Combine(_tempDir, "early-exit.json");
		VlmEvalOptions options = new()
		{
			FailOnAnyFixtureFailure = true,
			OutputFormat = ReportOutputFormat.Json,
			ReportPath = reportPath,
		};
		EvalRunner runner = BuildRunner(missing, ollamaReachable: true, extractionResult: null, options);

		int exit = await runner.RunAsync(missing, CancellationToken.None);

		exit.Should().Be(1);
		File.Exists(reportPath).Should().BeTrue();
		using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(reportPath));
		doc.RootElement.GetProperty("fixtures").GetArrayLength().Should().Be(0);
		doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32().Should().Be(0);
	}

	private void WriteFixture(string name, ExpectedReceipt expected)
	{
		string imagePath = Path.Combine(_tempDir, name);
		File.WriteAllBytes(imagePath, [0xFF, 0xD8, 0xFF]); // JPEG magic
		string sidecar = imagePath + ".expected.json";
		File.WriteAllText(sidecar, JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static ParsedReceipt MakeParsed(string store) =>
		new(
			StoreName: FieldConfidence<string>.High(store),
			Date: FieldConfidence<DateOnly>.None(),
			Items: [],
			Subtotal: FieldConfidence<decimal>.None(),
			TaxLines: [],
			Total: FieldConfidence<decimal>.None(),
			PaymentMethod: FieldConfidence<string?>.None());

	private static EvalRunner BuildRunner(
		string fixturesDir,
		bool ollamaReachable,
		ParsedReceipt? extractionResult,
		VlmEvalOptions options,
		IReceiptExtractionService? extractionServiceOverride = null)
	{
		// Stub the Ollama probe to either succeed or fail — the runner only cares that
		// IsOllamaReachableAsync returns true/false, and the probe is a single GET to /api/tags.
		Mock<HttpMessageHandler> probeHandler = new();
		probeHandler
			.Protected()
			.Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
			.ReturnsAsync(new HttpResponseMessage(ollamaReachable ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));

		Mock<IHttpClientFactory> httpFactory = new();
		httpFactory
			.Setup(f => f.CreateClient(It.IsAny<string>()))
			.Returns(() => new HttpClient(probeHandler.Object));

		VlmOcrOptions vlmOptions = new()
		{
			OllamaUrl = "http://localhost:11434",
		};

		IReceiptExtractionService extractionService = extractionServiceOverride ?? BuildExtractionService(extractionResult);

		FixtureLoader loader = new(NullLogger<FixtureLoader>.Instance);
		FixtureEvaluator evaluator = new(extractionService, options, NullLogger<FixtureEvaluator>.Instance);
		Reporter reporter = new(options, NullLogger<Reporter>.Instance);

		return new EvalRunner(
			httpFactory.Object,
			loader,
			evaluator,
			reporter,
			options,
			NullLogger<EvalRunner>.Instance,
			Options.Create(vlmOptions));
	}

	private static IReceiptExtractionService BuildExtractionService(ParsedReceipt? result)
	{
		Mock<IReceiptExtractionService> mock = new();
		if (result is not null)
		{
			mock
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(result);
		}
		return mock.Object;
	}
}
