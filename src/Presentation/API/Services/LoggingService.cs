using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace API.Services;

public static class LoggingService
{
	public static WebApplicationBuilder AddLoggingService(this WebApplicationBuilder builder)
	{
		LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
			.Enrich.FromLogContext();

		loggerConfiguration
			.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
			.MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information);

		string outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}";

		if (builder.Environment.IsDevelopment())
		{
			loggerConfiguration
				.WriteTo.Console(outputTemplate: outputTemplate)
				.MinimumLevel.Debug();
		}
		else
		{
			loggerConfiguration
				.WriteTo.Console(outputTemplate: outputTemplate)
				.MinimumLevel.Information();
		}

		// Export logs over OTLP only when an endpoint is configured: the Aspire dashboard in
		// development, or an external collector in production (docs/observability.md).
		Action<BatchedOpenTelemetrySinkOptions>? otlpSink =
			OtlpLogSinkConfiguration.Create(builder.Configuration, builder.Environment.ApplicationName);
		if (otlpSink is not null)
		{
			// null: OtlpLogSinkConfiguration is the only source, so the sink must not
			// re-read the process environment on top of it.
			loggerConfiguration.WriteTo.OpenTelemetry(otlpSink, getConfigurationVariable: null);
		}

		Log.Logger = loggerConfiguration.CreateLogger();

		builder.Host.UseSerilog(Log.Logger);

		builder.Services.AddLogging(loggingBuilder =>
		{
			loggingBuilder.ClearProviders();
			loggingBuilder.AddSerilog(Log.Logger);
		});

		Log.Information("Starting Receipts API");

		return builder;
	}
}
