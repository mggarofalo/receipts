using System.Net;
using System.Security.Claims;
using API.Configuration;
using API.Hubs;
using API.Middleware;
using API.Services;
using Application.Services;
using Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Sentry;

// Create builder
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddApplicationConfiguration();

// Configure Sentry error tracking (disabled when SENTRY_DSN is empty/missing)
builder.WebHost.UseSentry(o =>
{
	o.Dsn = builder.Configuration["SENTRY_BACKEND_DSN"] ?? builder.Configuration["SENTRY_DSN"] ?? "";
	o.Environment = builder.Environment.EnvironmentName;
	o.TracesSampleRate = 0.1;
	o.SendDefaultPii = false;
});

// Persist DataProtection keys: use /data volume in containers, default location in development
if (Directory.Exists("/data"))
{
	builder.Services.AddDataProtection()
		.PersistKeysToFileSystem(new DirectoryInfo("/data/DataProtection-Keys"));
}
else
{
	builder.Services.AddDataProtection();
}

// Register services
builder.Services
	.AddOpenApiServices()
	.AddVersioningServices()
	.AddApplicationServices(builder.Configuration)
	.AddCorsServices()
	.AddAuthServices(builder.Configuration)
	.RegisterProgramServices()
	.RegisterApplicationServices(builder.Configuration)
	.RegisterInfrastructureServices(builder.Configuration);

// Resolve the Ollama URL using the same fallback chain RegisterReceiptExtractionService uses,
// then register the named HttpClient the smoke test will pull from IHttpClientFactory. This
// replaces the previous pattern of `using HttpClient http = new()`, which bypassed
// IHttpClientFactory + OTel HTTP instrumentation + ConfigureHttpClientDefaults (RECEIPTS-635).
//
// The smoke-test HttpClient is registered unconditionally so DI is consistent across environments
// (tests inspect it). When the URL is unavailable, the smoke test simply skips at startup with a
// log warning — see the lifetime hook below.
string? smokeOllamaBaseUrl = Infrastructure.Services.InfrastructureService.ResolveOllamaUrl(builder.Configuration);

builder.Services.AddHttpClient(API.Services.VlmOcrSmokeTest.HttpClientName, client =>
{
	if (!string.IsNullOrWhiteSpace(smokeOllamaBaseUrl))
	{
		client.BaseAddress = new Uri(smokeOllamaBaseUrl.TrimEnd('/') + "/");
	}
	client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<API.Services.VlmOcrSmokeTest>();

// Build application
WebApplication app = builder.Build();

// Configure middleware
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
	ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
app.UseOpenApiServices()
   .UseApplicationServices()
   .UseOpenApiResponseValidation()
   .UseSentryTracing()
   .UseCorsServices()
   .UseAuthServices();

// Attach authenticated user context to Sentry scope for error attribution
app.Use(async (context, next) =>
{
	if (context.User.Identity?.IsAuthenticated == true)
	{
		SentrySdk.ConfigureScope(scope =>
		{
			scope.User = new SentryUser
			{
				Id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
				Email = context.User.FindFirst(ClaimTypes.Email)?.Value,
			};
		});
	}
	await next();
});

// Serve SPA static files in production (Vite dev server handles this in development)
if (!app.Environment.IsDevelopment())
{
	app.UseDefaultFiles();
	app.UseStaticFiles();
}

// Map Aspire health check endpoints (/health, /alive)
app.MapDefaultEndpoints();

// Map controllers
app.MapControllers();

// Map SignalR hubs
app.MapHub<EntityHub>("/hubs/entities");

// SPA fallback: serve index.html for client-side routes in production
if (!app.Environment.IsDevelopment())
{
	app.MapFallbackToFile("index.html");
}

// VLM OCR startup smoke test (RECEIPTS-616 epic): log-only, does not block startup.
// Runs once after the host is ready. The outer try/catch guards against exceptions that
// escape VlmOcrSmokeTest.RunAsync (e.g. UriFormatException from a malformed URL, or
// InvalidOperationException from a scheme-less one) which would otherwise become silent
// unobserved task exceptions.
//
// URL resolution mirrors RegisterReceiptExtractionService — both check Ocr:Vlm:OllamaUrl, then
// Ollama:BaseUrl. When neither is configured the smoke test is skipped and a startup warning is
// emitted so misconfigured deployments are visible in logs (RECEIPTS-635).
//
// RECEIPTS-652: the smoke test probes Ollama specifically (model-presence in /api/tags).
// When the Anthropic provider is selected via Ocr:Vlm:Provider=anthropic, there is no
// Ollama daemon to probe, so we skip the smoke test entirely. The Anthropic provider's
// readiness is enforced at startup via AnthropicOptions.ApiKey [Required] + ValidateOnStart.
string vlmProvider = (builder.Configuration[Common.ConfigurationVariables.OcrVlmProvider] ?? "ollama").ToLowerInvariant();
bool isOllamaProvider = string.Equals(vlmProvider, "ollama", StringComparison.Ordinal);

if (isOllamaProvider && !string.IsNullOrWhiteSpace(smokeOllamaBaseUrl))
{
	app.Lifetime.ApplicationStarted.Register(() =>
	{
		_ = Task.Run(async () =>
		{
			ILogger<Program> smokeLogger = app.Services.GetRequiredService<ILogger<Program>>();
			try
			{
				API.Services.VlmOcrSmokeTest smokeTest =
					app.Services.GetRequiredService<API.Services.VlmOcrSmokeTest>();
				await smokeTest.RunAsync(CancellationToken.None);
			}
			catch (Exception ex)
			{
				smokeLogger.LogWarning(ex, "VLM OCR: unexpected error during smoke test for {Url}", smokeOllamaBaseUrl);
			}
		});
	});
}
else if (!isOllamaProvider)
{
	app.Lifetime.ApplicationStarted.Register(() =>
	{
		ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
		startupLogger.LogInformation(
			"VLM OCR: skipping Ollama smoke test — provider={Provider}",
			vlmProvider);
	});
}
else
{
	app.Lifetime.ApplicationStarted.Register(() =>
	{
		ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
		startupLogger.LogWarning(
			"VLM OCR: skipping smoke test — neither {OcrVlmKey} nor {OllamaKey} is configured",
			Common.ConfigurationVariables.OcrVlmOllamaUrl,
			Common.ConfigurationVariables.OllamaBaseUrl);
	});
}

// Run application
await app.RunAsync();
