using System.Net;
using System.Security.Claims;
using API.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.API.Tests.Fixtures;

namespace Presentation.API.Tests.Configuration;

/// <summary>
/// Verifies that the auth middleware pipeline in <see cref="AuthConfiguration.UseAuthServices"/>
/// registers middleware in the correct order. Specifically, authentication must run before
/// rate limiting so that <c>context.User</c> has the BypassRateLimit claim when the limiter
/// evaluates, while authorization runs afterward so rejected requests still consume budget.
/// </summary>
public class AuthMiddlewareOrderingTests
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

	[Fact]
	public async Task UseAuthServices_AuthenticationRunsBeforeRateLimiter_BypassClaimIsAvailable()
	{
		// Arrange: Build a minimal host that uses UseAuthServices with a BypassRateLimit claim
		// injected before the production pipeline. If authentication runs before the rate limiter,
		// the claim will be visible to the rate limiter and bypass will work.
		using IHost host = CreateHostWithAuthPipeline(bypassRateLimit: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		// Act: Send more requests than the limit (permit = 1)
		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");
		HttpResponseMessage response3 = await client.GetAsync("/test");

		// Assert: All should succeed because the BypassRateLimit claim is available
		// to the rate limiter (authentication ran first, making the claim visible)
		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.OK);
		response3.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task UseAuthServices_WithoutBypassClaim_RateLimitIsEnforced()
	{
		// Arrange: Same pipeline but without the BypassRateLimit claim
		using IHost host = CreateHostWithAuthPipeline(bypassRateLimit: false);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		// Act
		HttpResponseMessage response1 = await client.GetAsync("/test");
		HttpResponseMessage response2 = await client.GetAsync("/test");

		// Assert: Rate limit should be enforced
		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	private static IHost CreateHostWithAuthPipeline(bool bypassRateLimit)
	{
		Dictionary<string, string?> configuration = new(RateLimitConfig)
		{
			["Jwt:Key"] = "test-key-that-is-at-least-32-characters-long-for-hmac",
			["Jwt:Issuer"] = "test-issuer",
			["Jwt:Audience"] = "test-audience",
		};
		WebApplicationBuilder appBuilder = ConfiguredApiTestHost.CreateBuilder(configuration);

		// Add only the services needed for auth + rate limiting
		appBuilder.Services.AddAuthServices(appBuilder.Configuration);
		appBuilder.Services.AddApplicationServices(appBuilder.Configuration);

		WebApplication app = appBuilder.Build();

		// Inject a test authentication identity with or without BypassRateLimit claim
		// BEFORE the auth pipeline. This simulates what the ApiKeyAuthenticationHandler does.
		app.Use(async (context, next) =>
		{
			ClaimsIdentity identity = new("TestApiKey");
			identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user-id"));
			if (bypassRateLimit)
			{
				identity.AddClaim(new Claim("BypassRateLimit", "true"));
			}

			context.User = new ClaimsPrincipal(identity);
			await next();
		});

		// Use the actual auth pipeline under test
		app.UseAuthServices();

		app.MapGet("/test", () => Results.Ok("OK"));

		return app;
	}
}
