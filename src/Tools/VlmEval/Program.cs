using Application.Interfaces.Services;
using Common;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VlmEval;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

VlmEvalOptions evalOptions = new();
builder.Configuration.GetSection("VlmEval").Bind(evalOptions);

// CLI args take precedence over env/appsettings — typical for tools where you want to override
// just one knob (--report-path, --provider) without unsetting it from your shell. Supported:
//   --output console|json|markdown   (sets VlmEval:OutputFormat)
//   --report-path <path>             (sets VlmEval:ReportPath)
//   --provider ollama|anthropic      (sets VlmEval:Provider)
// Unknown flags are ignored to remain compatible with future hosts (e.g. Aspire) that may pass
// extra args.
ParseCliArgs(args, evalOptions);

string provider = string.IsNullOrWhiteSpace(evalOptions.Provider)
	? "ollama"
	: evalOptions.Provider.ToLowerInvariant();

// Register the matching production-shape VLM client. Keeping the same registration helpers used
// by the API ensures eval results reflect production behavior (retry + circuit breaker +
// per-attempt timeout, with the standard ServiceDefaults handler removed). See RECEIPTS-639
// (Ollama) and RECEIPTS-652 (Anthropic).
switch (provider)
{
	case "ollama":
		{
			VlmOcrOptions vlmOptions = new();
			builder.Configuration.GetSection(ConfigurationVariables.OcrVlmSection).Bind(vlmOptions);

			if (string.IsNullOrWhiteSpace(vlmOptions.OllamaUrl))
			{
				vlmOptions.OllamaUrl = builder.Configuration[ConfigurationVariables.OllamaBaseUrl]
					?? "http://localhost:11434";
			}

			if (evalOptions.OllamaTimeoutSeconds > 0)
			{
				vlmOptions.TimeoutSeconds = evalOptions.OllamaTimeoutSeconds;
			}

			builder.Services.AddVlmOcrClient(vlmOptions);
			break;
		}
	case "anthropic":
		{
			AnthropicOptions anthropicOptions = new();
			builder.Configuration.GetSection(ConfigurationVariables.AnthropicSection).Bind(anthropicOptions);

			// Per-call timeout reuses the same VlmEval-level knob the Ollama path does so users can
			// give long fixtures the same headroom regardless of provider. Anthropic Haiku typically
			// returns in <20s but cold-start fixtures can spike, hence the same cushion.
			if (evalOptions.OllamaTimeoutSeconds > 0)
			{
				anthropicOptions.TimeoutSeconds = evalOptions.OllamaTimeoutSeconds;
			}

			builder.Services.AddAnthropicVlmClient(anthropicOptions);
			break;
		}
	default:
		throw new InvalidOperationException(
			$"Unknown VLM provider '{provider}'. Set VlmEval:Provider (or --provider) to 'ollama' or 'anthropic'.");
}

string fixturesPath = Path.GetFullPath(evalOptions.FixturesPath);

builder.Services.AddSingleton(evalOptions);

builder.Services.AddHttpClient("ollama-probe");

builder.Services.AddSingleton<FixtureLoader>();
builder.Services.AddSingleton<FixtureEvaluator>();
builder.Services.AddSingleton<Reporter>();
builder.Services.AddSingleton<EvalRunner>();

IHost host = builder.Build();

try
{
	await host.StartAsync();

	EvalRunner runner = host.Services.GetRequiredService<EvalRunner>();
	IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
	int exitCode = await runner.RunAsync(fixturesPath, lifetime.ApplicationStopping);

	await host.StopAsync();
	return exitCode;
}
catch (Exception ex)
{
	ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VlmEval");
	logger.LogCritical(ex, "VlmEval terminated with an unhandled exception.");
	return 1;
}

static void ParseCliArgs(string[] args, VlmEvalOptions options)
{
	for (int i = 0; i < args.Length; i++)
	{
		string arg = args[i];
		if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
		{
			string value = args[++i];
			if (Enum.TryParse(value, ignoreCase: true, out ReportOutputFormat format))
			{
				options.OutputFormat = format;
			}
		}
		else if (string.Equals(arg, "--report-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
		{
			options.ReportPath = args[++i];
		}
		else if (string.Equals(arg, "--provider", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
		{
			options.Provider = args[++i];
		}
	}
}
