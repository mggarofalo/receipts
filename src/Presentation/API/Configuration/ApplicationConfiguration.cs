using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using API.Filters;
using API.Hubs;
using API.Middleware;
using API.Services;
using API.Validators;
using Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

namespace API.Configuration;

public static class ApplicationConfiguration
{
	public static WebApplicationBuilder AddApplicationConfiguration(this WebApplicationBuilder builder)
	{
		if (builder.Environment.IsDevelopment())
		{
			builder.Configuration
				.SetBasePath(Directory.GetCurrentDirectory())
				.AddJsonFile($"appsettings.{Environments.Development}.json", optional: true, reloadOnChange: true)
				.AddUserSecrets<Program>(optional: true);
		}

		builder.Configuration.AddEnvironmentVariables();
		builder.AddLoggingService();

		return builder;
	}

	public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddValidatorsFromAssemblyContaining<CreateReceiptRequestValidator>();

		services.AddControllers(options =>
			{
				options.Filters.Add<FluentValidationActionFilter>();
				options.Filters.Add<ResourceIdResultFilter>();
			})
			.AddJsonOptions(options =>
			{
				options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
				options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
			});

		services.AddResponseCompression(options =>
		{
			options.EnableForHttps = true;
			options.Providers.Add<BrotliCompressionProvider>();
			options.Providers.Add<GzipCompressionProvider>();
		});
		services.Configure<BrotliCompressionProviderOptions>(options =>
			options.Level = CompressionLevel.Fastest);
		services.Configure<GzipCompressionProviderOptions>(options =>
			options.Level = CompressionLevel.SmallestSize);

		RateLimitingOptions rateLimitConfig = new();
		configuration.GetSection(RateLimitingOptions.SectionName).Bind(rateLimitConfig);

		services.AddRateLimiter(options =>
		{
			options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
			{
				if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
				{
					return RateLimitPartition.GetNoLimiter<string>("bypass");
				}

				return RateLimitPartition.GetSlidingWindowLimiter(
					context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
					_ => new SlidingWindowRateLimiterOptions
					{
						PermitLimit = rateLimitConfig.Global.PermitLimit,
						Window = TimeSpan.FromMinutes(rateLimitConfig.Global.WindowMinutes),
						SegmentsPerWindow = rateLimitConfig.Global.SegmentsPerWindow,
					});
			});

			options.AddPolicy("auth", context =>
			{
				if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
				{
					return RateLimitPartition.GetNoLimiter<string>("bypass");
				}

				return RateLimitPartition.GetFixedWindowLimiter(
					context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
					_ => new FixedWindowRateLimiterOptions
					{
						PermitLimit = rateLimitConfig.Auth.PermitLimit,
						Window = TimeSpan.FromMinutes(rateLimitConfig.Auth.WindowMinutes),
					});
			});

			options.AddPolicy("auth-sensitive", context =>
			{
				if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
				{
					return RateLimitPartition.GetNoLimiter<string>("bypass");
				}

				return RateLimitPartition.GetFixedWindowLimiter(
					context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
					_ => new FixedWindowRateLimiterOptions
					{
						PermitLimit = rateLimitConfig.AuthSensitive.PermitLimit,
						Window = TimeSpan.FromMinutes(rateLimitConfig.AuthSensitive.WindowMinutes),
					});
			});

			options.AddPolicy("api-key", context =>
			{
				if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
				{
					return RateLimitPartition.GetNoLimiter<string>("bypass");
				}

				return RateLimitPartition.GetFixedWindowLimiter(
					context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
					_ => new FixedWindowRateLimiterOptions
					{
						PermitLimit = rateLimitConfig.ApiKey.PermitLimit,
						Window = TimeSpan.FromMinutes(rateLimitConfig.ApiKey.WindowMinutes),
					});
			});

			options.OnRejected = async (context, cancellationToken) =>
			{
				string ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
				string path = context.HttpContext.Request.Path;
				string userAgent = context.HttpContext.Request.Headers.UserAgent.ToString();

				ILoggerFactory loggerFactory = context.HttpContext.RequestServices
					.GetRequiredService<ILoggerFactory>();
				Microsoft.Extensions.Logging.ILogger logger = loggerFactory.CreateLogger("API.RateLimiting");

				// Structured log format for fail2ban parsing
				logger.LogWarning(
					"RateLimitExceeded: IpAddress={IpAddress} Path={Path} UserAgent={UserAgent}",
					ip, path, userAgent);

				// Log to auth audit trail
				try
				{
					IAuthAuditService auditService = context.HttpContext.RequestServices
						.GetRequiredService<IAuthAuditService>();
					string? userId = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
					await auditService.LogAsync(new AuthAuditEntryDto(
						Guid.NewGuid(),
						nameof(Common.AuthEventType.RateLimitExceeded),
						userId,
						null,
						null,
						false,
						$"Rate limit exceeded on {path}",
						ip,
						userAgent,
						DateTimeOffset.UtcNow,
						JsonSerializer.Serialize(new { path, policy = context.Lease.TryGetMetadata(MetadataName.ReasonPhrase, out string? reason) ? reason : null })),
						cancellationToken);
				}
				catch
				{
					// Don't fail the response if audit logging fails
				}

				// Set Retry-After header
				if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
				{
					context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
				}
				else
				{
					context.HttpContext.Response.Headers.RetryAfter = "60";
				}

				context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
				await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Try again later.", cancellationToken);
			};
		});

		return services;
	}

	public static WebApplication UseApplicationServices(this WebApplication app)
	{
		app.UseResponseCompression();
		app.UseSerilogRequestLogging(options =>
		{
			options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
			{
				diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
				diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
				string? userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
				if (userId is not null)
				{
					diagnosticContext.Set("UserId", userId);
				}
			};

			// 503 on YNAB endpoints is expected when YNAB_PAT is not configured — log as Warning not Error
			options.GetLevel = (httpContext, elapsed, ex) =>
			{
				if (httpContext.Response.StatusCode == 503
					&& httpContext.Request.Path.StartsWithSegments("/api/ynab"))
				{
					return Serilog.Events.LogEventLevel.Warning;
				}

				return ex is not null || httpContext.Response.StatusCode >= 500
					? Serilog.Events.LogEventLevel.Error
					: Serilog.Events.LogEventLevel.Information;
			};
		});
		app.UseMiddleware<ValidationExceptionMiddleware>();
		if (app.Environment.IsDevelopment())
		{
			app.UseHttpsRedirection();
		}

		app.UseRouting();

		return app;
	}
}