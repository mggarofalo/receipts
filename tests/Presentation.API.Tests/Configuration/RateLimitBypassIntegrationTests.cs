using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using API.Configuration;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Presentation.API.Tests.Fixtures;

namespace Presentation.API.Tests.Configuration;

/// <summary>
/// Integration tests that verify the full auth + rate limiting pipeline works end-to-end.
/// These send actual <c>X-API-Key</c> headers through the real <see cref="AuthConfiguration"/>
/// pipeline (PolicyScheme → ApiKeyAuthenticationHandler → rate limiter) to prove that
/// the <c>BypassRateLimit</c> claim is available when the rate limiter evaluates.
/// </summary>
[Trait("Category", "Integration")]
public class RateLimitBypassIntegrationTests
{
	private const string BypassApiKey = "bypass-test-key";
	private const string NormalApiKey = "normal-test-key";
	private const string TestUserId = "test-user-id";

	private static readonly Dictionary<string, string?> TestConfig = new()
	{
		// Very low rate limits to make bypass observable
		["RateLimiting:Global:PermitLimit"] = "2",
		["RateLimiting:Global:WindowMinutes"] = "1",
		["RateLimiting:Global:SegmentsPerWindow"] = "4",
		["RateLimiting:Auth:PermitLimit"] = "2",
		["RateLimiting:Auth:WindowMinutes"] = "1",
		["RateLimiting:AuthSensitive:PermitLimit"] = "2",
		["RateLimiting:AuthSensitive:WindowMinutes"] = "1",
		["RateLimiting:ApiKey:PermitLimit"] = "2",
		["RateLimiting:ApiKey:WindowMinutes"] = "1",
		// JWT config (required by AddAuthServices)
		["Jwt:Key"] = "test-key-that-is-at-least-32-characters-long-for-hmac-sha256",
		["Jwt:Issuer"] = "test-issuer",
		["Jwt:Audience"] = "test-audience",
	};

	[Fact]
	public async Task ApiKeyWithBypass_ExceedsGlobalLimit_AllRequestsSucceed()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		HttpClient client = host.GetTestClient();
		client.DefaultRequestHeaders.Add("X-API-Key", BypassApiKey);

		List<HttpResponseMessage> responses = [];
		for (int i = 0; i < 5; i++)
		{
			responses.Add(await client.GetAsync("/api/test"));
		}

		responses.Should().AllSatisfy(r =>
			r.StatusCode.Should().Be(HttpStatusCode.OK,
				"API key with BypassRateLimit=true should bypass the global rate limit"));
	}

	[Fact]
	public async Task ApiKeyWithoutBypass_ExceedsGlobalLimit_Gets429()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		HttpClient client = host.GetTestClient();
		client.DefaultRequestHeaders.Add("X-API-Key", NormalApiKey);

		List<HttpResponseMessage> responses = [];
		for (int i = 0; i < 5; i++)
		{
			responses.Add(await client.GetAsync("/api/test"));
		}

		responses.Take(2).Should().AllSatisfy(r =>
			r.StatusCode.Should().Be(HttpStatusCode.OK));
		responses.Skip(2).Should().Contain(r =>
			r.StatusCode == HttpStatusCode.TooManyRequests,
			"API key without BypassRateLimit should be rate limited");
		HttpResponseMessage rejection = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
		await AssertRateLimitProblemAsync(rejection, "/api/test");
	}

	[Fact]
	public async Task AnonymousRequest_ExceedsGlobalLimit_Gets429()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		List<HttpResponseMessage> responses = [];
		for (int i = 0; i < 5; i++)
		{
			responses.Add(await client.GetAsync("/api/test-anon"));
		}

		responses.Take(2).Should().AllSatisfy(r =>
			r.StatusCode.Should().Be(HttpStatusCode.OK));
		responses.Skip(2).Should().Contain(r =>
			r.StatusCode == HttpStatusCode.TooManyRequests,
			"Anonymous requests should still be rate limited");
	}

	[Fact]
	public async Task AnonymousRequest_UnsupportedAccept_RateLimitStillReturnsProblemResponse()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		using HttpClient client = host.GetTestClient();
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

		List<HttpResponseMessage> responses = [];
		for (int i = 0; i < 5; i++)
		{
			responses.Add(await client.GetAsync("/api/test-anon"));
		}

		HttpResponseMessage rejection = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
		await AssertRateLimitProblemAsync(rejection, "/api/test-anon");
	}

	private static IHost CreateHost()
	{
		WebApplicationBuilder appBuilder = ConfiguredApiTestHost.CreateBuilder(TestConfig);

		// Register the real auth + rate limiting services
		appBuilder.Services.AddAuthServices(appBuilder.Configuration);
		appBuilder.Services.AddApplicationServices(appBuilder.Configuration);

		// Mock IApiKeyService to return bypass/non-bypass results based on the key
		Mock<IApiKeyService> apiKeyService = new();
		apiKeyService
			.Setup(s => s.GetUserIdByApiKeyAsync(BypassApiKey))
			.ReturnsAsync(new ApiKeyValidationResult(TestUserId, Guid.NewGuid(), true));
		apiKeyService
			.Setup(s => s.GetUserIdByApiKeyAsync(NormalApiKey))
			.ReturnsAsync(new ApiKeyValidationResult(TestUserId, Guid.NewGuid(), false));
		appBuilder.Services.AddSingleton(apiKeyService.Object);

		// Mock other dependencies required by ApiKeyAuthenticationHandler
		appBuilder.Services.AddSingleton(new Mock<IAuthAuditService>().Object);
		appBuilder.Services.AddSingleton(CreateMockUserManager());

		WebApplication app = appBuilder.Build();

		// Use the actual auth pipeline under test
		app.UseAuthServices();

		app.MapGet("/api/test", () => Results.Ok("OK"))
			.AllowAnonymous();
		app.MapGet("/api/test-anon", () => Results.Ok("OK"))
			.AllowAnonymous();

		return app;
	}

	private static UserManager<ApplicationUser> CreateMockUserManager()
	{
		Mock<IUserStore<ApplicationUser>> store = new();
		Mock<UserManager<ApplicationUser>> manager = new(
			store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
		manager
			.Setup(m => m.FindByIdAsync(TestUserId))
			.ReturnsAsync(new ApplicationUser { Id = TestUserId, Email = "test@test.com" });
		manager
			.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>()))
			.ReturnsAsync(new List<string> { "Admin" });
		return manager.Object;
	}

	private static async Task AssertRateLimitProblemAsync(HttpResponseMessage response, string instance)
	{
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryAfterValues)
			.Should().BeTrue();
		int retryAfter = int.Parse(retryAfterValues!.Single());
		retryAfter.Should().BeGreaterThan(0);

		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("status").GetInt32().Should().Be(429);
		body.RootElement.GetProperty("title").GetString().Should().Be("Too Many Requests");
		body.RootElement.GetProperty("detail").GetString().Should().Be("Rate limit exceeded. Try again later.");
		body.RootElement.GetProperty("type").GetString()
			.Should().Be("https://www.rfc-editor.org/rfc/rfc6585#section-4");
		body.RootElement.GetProperty("instance").GetString().Should().Be(instance);
		body.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfter);
	}
}
