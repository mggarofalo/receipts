using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
	private const string HealthEndpointPath = "/health";
	private const string AlivenessEndpointPath = "/alive";

	public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
	{
		builder.ConfigureOpenTelemetry();

		builder.AddDefaultHealthChecks();

		builder.Services.AddServiceDiscovery();

		builder.Services.ConfigureHttpClientDefaults(http =>
		{
			// Turn on resilience by default
			http.AddStandardResilienceHandler(options =>
			{
				options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
				options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
				options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
				options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
			});

			// Turn on service discovery by default
			http.AddServiceDiscovery();
		});

		// Uncomment the following to restrict the allowed schemes for service discovery.
		// builder.Services.Configure<ServiceDiscoveryOptions>(options =>
		// {
		//     options.AllowedSchemes = ["https"];
		// });

		return builder;
	}

	public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
	{
		builder.Logging.AddOpenTelemetry(logging =>
		{
			logging.IncludeFormattedMessage = true;
			logging.IncludeScopes = true;
		});

		builder.Services.AddOpenTelemetry()
			.WithMetrics(metrics =>
			{
				metrics.AddAspNetCoreInstrumentation()
					.AddHttpClientInstrumentation()
					.AddRuntimeInstrumentation()
					.AddMeter("Receipts.Embeddings");
			})
			.WithTracing(tracing =>
			{
				tracing.AddSource(builder.Environment.ApplicationName)
					.AddAspNetCoreInstrumentation(tracing =>
						// Exclude health check requests from tracing
						tracing.Filter = context =>
							!context.Request.Path.StartsWithSegments(HealthEndpointPath)
							&& !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
					)
					// Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
					//.AddGrpcClientInstrumentation()
					.AddHttpClientInstrumentation()
					.AddEntityFrameworkCoreInstrumentation();
			});

		builder.AddOpenTelemetryExporters();

		return builder;
	}

	private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
	{
		var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

		if (useOtlpExporter)
		{
			builder.Services.AddOpenTelemetry().UseOtlpExporter();
		}

		// Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
		//if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
		//{
		//    builder.Services.AddOpenTelemetry()
		//       .UseAzureMonitor();
		//}

		return builder;
	}

	public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
	{
		builder.Services.AddHealthChecks()
			// Add a default liveness check to ensure app is responsive
			.AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

		return builder;
	}

	public static WebApplication MapDefaultEndpoints(this WebApplication app)
	{
		// Aspire's default template only exposed /health and /alive in Development because
		// health-check response bodies can leak internals (stack traces, per-check detail).
		// We expose them everywhere, but in non-Development we use a status-only response
		// writer that writes just Healthy/Degraded/Unhealthy as plain text — no payload.
		// See https://aka.ms/dotnet/aspire/healthchecks for the underlying guidance.
		if (app.Environment.IsDevelopment())
		{
			// Full payload in Dev (useful in Aspire dashboard for debugging).
			app.MapHealthChecks(HealthEndpointPath);
			app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
			{
				Predicate = r => r.Tags.Contains("live")
			});
		}
		else
		{
			app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
			{
				ResponseWriter = WriteStatusOnlyAsync,
			});
			app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
			{
				Predicate = r => r.Tags.Contains("live"),
				ResponseWriter = WriteStatusOnlyAsync,
			});
		}

		return app;
	}

	private static Task WriteStatusOnlyAsync(HttpContext context, HealthReport report)
	{
		context.Response.ContentType = "text/plain; charset=utf-8";
		return context.Response.WriteAsync(report.Status.ToString());
	}
}
