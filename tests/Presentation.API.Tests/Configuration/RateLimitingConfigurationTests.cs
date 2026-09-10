using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using API.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Presentation.API.Tests.Configuration;

/// <summary>
/// Tests the rate limiter configuration in isolation — verifies that each rate limit policy
/// (global, auth, auth-sensitive, api-key) correctly enforces or bypasses limits based on
/// the BypassRateLimit claim. These tests inject claims via inline middleware before the
/// rate limiter, so they do NOT test the full auth pipeline ordering. For pipeline ordering
/// tests, see <see cref="AuthMiddlewareOrderingTests"/> and
/// <see cref="RateLimitBypassIntegrationTests"/>.
/// </summary>
public class RateLimitingConfigurationTests
{
	private static readonly Dictionary<string, string?> RateLimitConfig = new()
	{
		["RateLimiting:Global:PermitLimit"] = "1",
		["RateLimiting:Global:WindowMinutes"] = "1",
		["RateLimiting:Global:SegmentsPerWindow"] = "4",
		["RateLimiting:Auth:PermitLimit"] = "1",
		["RateLimiting:Auth:WindowMinutes"] = "1",
		["RateLimiting:AuthSensitive:PermitLimit"] = "1",
		["RateLimiting:AuthSensitive:WindowMinutes"] = "1",
		["RateLimiting:ApiKey:PermitLimit"] = "1",
		["RateLimiting:ApiKey:WindowMinutes"] = "1",
	};

	[Theory]
	[InlineData("auth")]
	[InlineData("auth-sensitive")]
	[InlineData("api-key")]
	public async Task PolicyBypassesRateLimit_WhenBypassClaimIsTrue(string policyName)
	{
		using IHost host = CreateHost(policyName, bypassRateLimit: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");
		HttpResponseMessage response3 = await client.GetAsync("/test");

		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.OK);
		response3.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData("auth")]
	[InlineData("auth-sensitive")]
	[InlineData("api-key")]
	public async Task PolicyEnforcesRateLimit_WhenBypassClaimIsFalse(string policyName)
	{
		using IHost host = CreateHost(policyName, bypassRateLimit: false);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");

		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Fact]
	public async Task GlobalLimiterBypassesRateLimit_WhenBypassClaimIsTrue()
	{
		using IHost host = CreateHost(policyName: null, bypassRateLimit: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");
		HttpResponseMessage response3 = await client.GetAsync("/test");

		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.OK);
		response3.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task GlobalLimiterEnforcesRateLimit_WhenBypassClaimIsFalse()
	{
		using IHost host = CreateHost(policyName: null, bypassRateLimit: false);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");

		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	private static IHost CreateHost(string? policyName, bool bypassRateLimit)
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(RateLimitConfig)
			.Build();

		RateLimitingOptions rateLimitConfig = new();
		configuration.GetSection(RateLimitingOptions.SectionName).Bind(rateLimitConfig);

		WebApplicationBuilder appBuilder = WebApplication.CreateBuilder();
		appBuilder.WebHost.UseTestServer();

		appBuilder.Services.AddRateLimiter(options =>
		{
			ConfigureGlobalLimiter(options, rateLimitConfig, applyGlobalLimit: policyName == null);
			ConfigureNamedPolicies(options, rateLimitConfig);
			options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
		});

		WebApplication app = appBuilder.Build();

		// Inject the BypassRateLimit claim before rate limiting
		app.Use(async (context, next) =>
		{
			if (bypassRateLimit)
			{
				ClaimsIdentity identity = new("TestAuth");
				identity.AddClaim(new Claim("BypassRateLimit", "true"));
				context.User = new ClaimsPrincipal(identity);
			}

			await next();
		});

		app.UseRateLimiter();

		if (policyName != null)
		{
			app.MapGet("/test", () => Results.Ok("OK"))
				.RequireRateLimiting(policyName);
		}
		else
		{
			app.MapGet("/test", () => Results.Ok("OK"));
		}

		return app;
	}

	private static void ConfigureGlobalLimiter(RateLimiterOptions options, RateLimitingOptions config, bool applyGlobalLimit)
	{
		options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
		{
			if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
			{
				return RateLimitPartition.GetNoLimiter<string>("bypass");
			}

			if (!applyGlobalLimit)
			{
				return RateLimitPartition.GetNoLimiter<string>("no-global");
			}

			return RateLimitPartition.GetSlidingWindowLimiter(
				context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
				_ => new SlidingWindowRateLimiterOptions
				{
					PermitLimit = config.Global.PermitLimit,
					Window = TimeSpan.FromMinutes(config.Global.WindowMinutes),
					SegmentsPerWindow = config.Global.SegmentsPerWindow,
				});
		});
	}

	private static void ConfigureNamedPolicies(RateLimiterOptions options, RateLimitingOptions config)
	{
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
					PermitLimit = config.Auth.PermitLimit,
					Window = TimeSpan.FromMinutes(config.Auth.WindowMinutes),
				});
		});

		options.AddPolicy("auth-sensitive", context =>
		{
			if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
			{
				return RateLimitPartition.GetNoLimiter<string>("bypass");
			}

			return RateLimitPartition.GetFixedWindowLimiter(
				GetIdentityRateLimitKey(context),
				_ => new FixedWindowRateLimiterOptions
				{
					PermitLimit = config.AuthSensitive.PermitLimit,
					Window = TimeSpan.FromMinutes(config.AuthSensitive.WindowMinutes),
				});
		});

		options.AddPolicy("api-key", context =>
		{
			if (context.User.FindFirst("BypassRateLimit")?.Value == "true")
			{
				return RateLimitPartition.GetNoLimiter<string>("bypass");
			}

			return RateLimitPartition.GetFixedWindowLimiter(
				GetIdentityRateLimitKey(context),
				_ => new FixedWindowRateLimiterOptions
				{
					PermitLimit = config.ApiKey.PermitLimit,
					Window = TimeSpan.FromMinutes(config.ApiKey.WindowMinutes),
				});
		});
	}

	private static string GetIdentityRateLimitKey(HttpContext context)
	{
		string? userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		return !string.IsNullOrWhiteSpace(userId)
			? $"user:{userId}"
			: $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
	}
}
