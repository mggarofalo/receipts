using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using API.Configuration;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
	private const string SecondUserApiKey = "second-user-test-key";
	private const string InvalidApiKey = "invalid-test-key";
	private const string TestUserId = "test-user-id";
	private const string SecondUserId = "second-user-id";
	private const string TestSecurityStamp = "current-security-stamp";

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
	public async Task ProtectedRequest_WithoutCredentials_Returns401UntilGlobalLimitThen429()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		using HttpClient client = host.GetTestClient();

		HttpResponseMessage response1 = await client.GetAsync("/api/protected");
		HttpResponseMessage response2 = await client.GetAsync("/api/protected");
		HttpResponseMessage response3 = await client.GetAsync("/api/protected");

		response1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Fact]
	public async Task ProtectedRequest_WithValidApiKeyOnly_Returns200UntilGlobalLimitThen429()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		using HttpClient client = CreateApiKeyClient(host, NormalApiKey);

		HttpResponseMessage response1 = await client.GetAsync("/api/protected");
		HttpResponseMessage response2 = await client.GetAsync("/api/protected");
		HttpResponseMessage response3 = await client.GetAsync("/api/protected");

		response1.StatusCode.Should().Be(HttpStatusCode.OK);
		response2.StatusCode.Should().Be(HttpStatusCode.OK);
		response3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Fact]
	public async Task ProtectedRequest_WithValidJwtOnly_IsAuthorized()
	{
		using IHost host = CreateHost(globalPermitLimit: 100);
		await host.StartAsync();
		using HttpClient client = CreateJwtClient(host, MintToken(TestUserId));

		HttpResponseMessage response = await client.GetAsync("/api/protected");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task ForbiddenRequest_WithAuthentication_Returns403UntilGlobalLimitThen429()
	{
		using IHost host = CreateHost();
		await host.StartAsync();
		using HttpClient client = CreateApiKeyClient(host, NormalApiKey);

		HttpResponseMessage response1 = await client.GetAsync("/api/admin-only");
		HttpResponseMessage response2 = await client.GetAsync("/api/admin-only");
		HttpResponseMessage response3 = await client.GetAsync("/api/admin-only");

		response1.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response2.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
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

	[Theory]
	[InlineData("/api/auth-sensitive")]
	[InlineData("/api/api-key-sensitive")]
	public async Task IdentityAwarePolicy_UsesSeparateBudgetsPerAuthenticatedUser(string path)
	{
		using IHost host = CreateHost(globalPermitLimit: 100);
		await host.StartAsync();
		using HttpClient firstUser = CreateApiKeyClient(host, NormalApiKey);
		using HttpClient secondUser = CreateApiKeyClient(host, SecondUserApiKey);

		HttpResponseMessage firstUserResponse1 = await firstUser.GetAsync(path);
		HttpResponseMessage firstUserResponse2 = await firstUser.GetAsync(path);
		HttpResponseMessage firstUserResponse3 = await firstUser.GetAsync(path);
		HttpResponseMessage secondUserResponse1 = await secondUser.GetAsync(path);
		HttpResponseMessage secondUserResponse2 = await secondUser.GetAsync(path);
		HttpResponseMessage secondUserResponse3 = await secondUser.GetAsync(path);

		firstUserResponse1.StatusCode.Should().Be(HttpStatusCode.OK);
		firstUserResponse2.StatusCode.Should().Be(HttpStatusCode.OK);
		firstUserResponse3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
			"requests from the same authenticated user share one named-policy budget");
		secondUserResponse1.StatusCode.Should().Be(HttpStatusCode.OK,
			"a different user behind the same IP has an independent named-policy budget");
		secondUserResponse2.StatusCode.Should().Be(HttpStatusCode.OK);
		secondUserResponse3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	[Fact]
	public async Task AuthSensitivePolicy_ValidJwtWithInvalidApiKey_FailsClosedAndUsesIpFallbackBudget()
	{
		using IHost host = CreateHost(globalPermitLimit: 100);
		await host.StartAsync();
		using HttpClient firstJwtSubject = CreateJwtClient(host, MintToken(TestUserId), InvalidApiKey);
		using HttpClient secondJwtSubject = CreateJwtClient(host, MintToken(SecondUserId), InvalidApiKey);

		HttpResponseMessage firstSubjectResponse = await firstJwtSubject.GetAsync("/api/auth-sensitive");
		HttpResponseMessage secondSubjectResponse1 = await secondJwtSubject.GetAsync("/api/auth-sensitive");
		HttpResponseMessage secondSubjectResponse2 = await secondJwtSubject.GetAsync("/api/auth-sensitive");

		firstSubjectResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
			"an API-key header selects API-key authentication even when the bearer token is valid");
		secondSubjectResponse1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		secondSubjectResponse2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
			"failed mixed-header authentication has no user identity, so both JWT subjects share the IP fallback budget");
	}

	private static IHost CreateHost(int globalPermitLimit = 2)
	{
		Dictionary<string, string?> configuration = new(TestConfig)
		{
			["RateLimiting:Global:PermitLimit"] = globalPermitLimit.ToString(),
		};
		WebApplicationBuilder appBuilder = ConfiguredApiTestHost.CreateBuilder(configuration);

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
		apiKeyService
			.Setup(s => s.GetUserIdByApiKeyAsync(SecondUserApiKey))
			.ReturnsAsync(new ApiKeyValidationResult(SecondUserId, Guid.NewGuid(), false));
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
		app.MapGet("/api/protected", () => Results.Ok("OK"))
			.RequireAuthorization();
		app.MapGet("/api/admin-only", () => Results.Ok("OK"))
			.RequireAuthorization("RequireAdmin");
		app.MapGet("/api/auth-sensitive", () => Results.Ok("OK"))
			.RequireAuthorization()
			.RequireRateLimiting("auth-sensitive");
		app.MapGet("/api/api-key-sensitive", () => Results.Ok("OK"))
			.RequireAuthorization()
			.RequireRateLimiting("api-key");

		return app;
	}

	private static HttpClient CreateApiKeyClient(IHost host, string apiKey)
	{
		HttpClient client = host.GetTestClient();
		client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
		return client;
	}

	private static HttpClient CreateJwtClient(IHost host, string token, string? apiKey = null)
	{
		HttpClient client = host.GetTestClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (apiKey is not null)
		{
			client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
		}

		return client;
	}

	private static string MintToken(string userId)
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(TestConfig)
			.Build();
		return new TokenService(configuration)
			.GenerateAccessToken(userId, $"{userId}@test.com", ["User"], false, TestSecurityStamp);
	}

	private static UserManager<ApplicationUser> CreateMockUserManager()
	{
		Mock<IUserStore<ApplicationUser>> store = new();
		Mock<UserManager<ApplicationUser>> manager = new(
			store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
		manager
			.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
			.ReturnsAsync((string userId) => new ApplicationUser
			{
				Id = userId,
				Email = $"{userId}@test.com",
				SecurityStamp = TestSecurityStamp,
			});
		manager
			.Setup(m => m.IsLockedOutAsync(It.IsAny<ApplicationUser>()))
			.ReturnsAsync(false);
		manager
			.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>()))
			.ReturnsAsync(new List<string> { "User" });
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
