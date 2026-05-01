using System.Diagnostics;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VlmEval;

public sealed class EvalRunner
{
	/// <summary>POSIX exit code for SIGINT (cancellation).</summary>
	private const int ExitCodeCancelled = 130;

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IOptions<VlmOcrOptions>? _vlmOptions;
	private readonly IOptions<AnthropicOptions>? _anthropicOptions;
	private readonly FixtureLoader _fixtureLoader;
	private readonly FixtureEvaluator _fixtureEvaluator;
	private readonly Reporter _reporter;
	private readonly VlmEvalOptions _options;
	private readonly ILogger<EvalRunner> _logger;

	public EvalRunner(
		IHttpClientFactory httpClientFactory,
		FixtureLoader fixtureLoader,
		FixtureEvaluator fixtureEvaluator,
		Reporter reporter,
		VlmEvalOptions options,
		ILogger<EvalRunner> logger,
		IOptions<VlmOcrOptions>? vlmOptions = null,
		IOptions<AnthropicOptions>? anthropicOptions = null)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(fixtureLoader);
		ArgumentNullException.ThrowIfNull(fixtureEvaluator);
		ArgumentNullException.ThrowIfNull(reporter);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_httpClientFactory = httpClientFactory;
		_fixtureLoader = fixtureLoader;
		_fixtureEvaluator = fixtureEvaluator;
		_reporter = reporter;
		_options = options;
		_logger = logger;
		_vlmOptions = vlmOptions;
		_anthropicOptions = anthropicOptions;
	}

	public async Task<int> RunAsync(string fixturesDirectory, CancellationToken cancellationToken)
	{
		string provider = string.IsNullOrWhiteSpace(_options.Provider)
			? "ollama"
			: _options.Provider.ToLowerInvariant();
		string providerEndpoint = ResolveProviderEndpoint(provider);
		DateTimeOffset startedAt = DateTimeOffset.UtcNow;
		_reporter.PrintHeader(provider, providerEndpoint, fixturesDirectory);

		// Missing fixtures dir is a configuration error: a typo'd FixturesPath looks identical
		// to "no fixtures yet" if we silently mkdir. Treat it as failure when the strict flag
		// is set; otherwise warn and exit 0 to preserve the "always green when flag off" contract.
		if (!Directory.Exists(fixturesDirectory))
		{
			_reporter.PrintMissingFixturesDirectory(fixturesDirectory);
			_reporter.WriteReport(
				new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
				results: [],
				totalElapsed: TimeSpan.Zero,
				cancelled: false);
			return _options.FailOnAnyFixtureFailure ? 1 : 0;
		}

		// Reachability probe is only meaningful for the Ollama provider — the Anthropic API is
		// SaaS, no preflight needed (the per-call resilience pipeline + auth header validation
		// handle outages). RECEIPTS-652.
		if (string.Equals(provider, "ollama", StringComparison.Ordinal)
			&& !await IsOllamaReachableAsync(cancellationToken))
		{
			_reporter.PrintOllamaUnreachable(providerEndpoint);
			_reporter.WriteReport(
				new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
				results: [],
				totalElapsed: TimeSpan.Zero,
				cancelled: false);
			return 1;
		}

		LoadedFixtures loaded = _fixtureLoader.LoadFrom(fixturesDirectory);

		if (loaded.Fixtures.Count == 0 && loaded.OrphanFiles.Count == 0)
		{
			_reporter.PrintEmptyFixturesDirectory(fixturesDirectory);
			_reporter.WriteReport(
				new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
				results: [],
				totalElapsed: TimeSpan.Zero,
				cancelled: false);
			return _options.FailOnAnyFixtureFailure ? 1 : 0;
		}

		foreach (string orphan in loaded.OrphanFiles)
		{
			_reporter.PrintOrphan(orphan);
		}

		if (loaded.Fixtures.Count == 0)
		{
			_reporter.PrintNoValidFixtures();
			_reporter.WriteReport(
				new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
				results: [],
				totalElapsed: TimeSpan.Zero,
				cancelled: false);
			return _options.FailOnAnyFixtureFailure ? 1 : 0;
		}

		Stopwatch total = Stopwatch.StartNew();
		List<FixtureResult> results = [];
		bool cancelled = false;
		foreach (Fixture fixture in loaded.Fixtures)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancelled = true;
				break;
			}

			FixtureResult result = await _fixtureEvaluator.EvaluateAsync(fixture, cancellationToken);
			_reporter.PrintFixtureResult(result);
			results.Add(result);

			// EvaluateAsync's broad catch blocks (file IO, VLM call) swallow
			// OperationCanceledException and return a normal failure result. Without this
			// post-iteration check, cancellation that fires during the LAST fixture would not
			// be detected — the loop would exit naturally and we'd return 0 or 1 instead of 130
			// (and the structured report's `cancelled` field would lie). RECEIPTS-634 follow-up.
			if (cancellationToken.IsCancellationRequested)
			{
				cancelled = true;
				break;
			}
		}
		total.Stop();

		if (cancelled)
		{
			_reporter.PrintCancelled(results.Count, loaded.Fixtures.Count);
			_reporter.WriteReport(
				new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
				results,
				total.Elapsed,
				cancelled: true);
			return ExitCodeCancelled;
		}

		_reporter.PrintSummary(results, total.Elapsed);
		_reporter.WriteReport(
			new RunInfo(startedAt, provider, providerEndpoint, fixturesDirectory),
			results,
			total.Elapsed,
			cancelled: false);

		if (!_options.FailOnAnyFixtureFailure)
		{
			return 0;
		}

		return results.Any(r => !r.Passed) ? 1 : 0;
	}

	private string ResolveProviderEndpoint(string provider)
	{
		return provider switch
		{
			"anthropic" => _anthropicOptions?.Value.BaseUrl ?? "(unset)",
			_ => _vlmOptions?.Value.OllamaUrl ?? "(unset)",
		};
	}

	private async Task<bool> IsOllamaReachableAsync(CancellationToken cancellationToken)
	{
		string? ollamaUrl = _vlmOptions?.Value.OllamaUrl;
		if (string.IsNullOrWhiteSpace(ollamaUrl))
		{
			return false;
		}

		using HttpClient probe = _httpClientFactory.CreateClient("ollama-probe");
		probe.BaseAddress = new Uri(ollamaUrl.TrimEnd('/') + "/");
		probe.Timeout = TimeSpan.FromSeconds(5);

		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(5));

		try
		{
			using HttpResponseMessage response = await probe.GetAsync("api/tags", cts.Token);
			return response.IsSuccessStatusCode;
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Ollama availability probe failed");
			return false;
		}
	}
}
