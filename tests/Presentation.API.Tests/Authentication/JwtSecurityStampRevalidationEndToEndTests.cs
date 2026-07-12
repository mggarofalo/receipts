using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Authentication;

/// <summary>
/// End-to-end proof that per-request security-stamp revalidation works through the REAL token
/// pipeline: a token minted by <see cref="TokenService"/> is validated by the actual JwtBearer
/// handler configured in <see cref="AuthConfiguration.AddAuthServices"/> (same
/// TokenValidationParameters, same <c>OnTokenValidated</c> handler), so this also proves the
/// <c>security_stamp</c> and <c>NameIdentifier</c> claims survive JwtBearer's inbound claim mapping —
/// something the mocked <see cref="JwtSecurityStampValidator.EvaluateAsync"/> unit tests cannot catch.
/// A stand-alone TestServer host is used (mirroring AuthMiddlewareOrderingTests); there is no shared
/// WebApplicationFactory in this repo.
/// </summary>
public class JwtSecurityStampRevalidationEndToEndTests
{
	private static readonly Dictionary<string, string?> JwtConfig = new()
	{
		["Jwt:Key"] = "test-signing-key-that-is-at-least-32-chars!!",
		["Jwt:Issuer"] = "test-issuer",
		["Jwt:Audience"] = "test-audience",
	};

	private static IConfiguration BuildConfiguration() =>
		new ConfigurationBuilder().AddInMemoryCollection(JwtConfig).Build();

	private static string MintToken(string userId, string securityStamp) =>
		new TokenService(BuildConfiguration())
			.GenerateAccessToken(userId, "test@example.com", new List<string> { "User" }, false, securityStamp);

	private static Mock<UserManager<ApplicationUser>> CreateUserManagerMock()
	{
		Mock<IUserStore<ApplicationUser>> userStoreMock = new();
		return new Mock<UserManager<ApplicationUser>>(
			userStoreMock.Object,
			new Mock<IOptions<IdentityOptions>>().Object,
			new Mock<IPasswordHasher<ApplicationUser>>().Object,
			Array.Empty<IUserValidator<ApplicationUser>>(),
			Array.Empty<IPasswordValidator<ApplicationUser>>(),
			new Mock<ILookupNormalizer>().Object,
			new Mock<IdentityErrorDescriber>().Object,
			new Mock<IServiceProvider>().Object,
			new Mock<ILogger<UserManager<ApplicationUser>>>().Object);
	}

	// Builds a minimal host wired with the REAL auth pipeline from AddAuthServices (the same
	// JwtBearer TokenValidationParameters, OnTokenValidated handler, and default "ApiOrJwt" policy the
	// app uses) plus a mock UserManager for the OnTokenValidated handler to resolve. The default policy
	// lists both the JwtBearer and ApiKey schemes, so the ApiKey handler is constructed too — its
	// collaborators are registered as no-op mocks. A request carrying only a Bearer token makes the
	// ApiKey handler return NoResult, leaving JwtBearer + security-stamp revalidation to decide the
	// outcome: a valid token yields 200 and a failed revalidation (context.Fail) yields 401.
	private static WebApplication BuildHost(UserManager<ApplicationUser> userManager)
	{
		WebApplicationBuilder appBuilder = WebApplication.CreateBuilder();
		appBuilder.WebHost.UseTestServer();

		appBuilder.Services.AddAuthServices(BuildConfiguration());
		appBuilder.Services.AddScoped(_ => userManager);
		// Collaborators the ApiKey scheme handler needs to be constructible under the "ApiOrJwt" policy.
		appBuilder.Services.AddSingleton(Mock.Of<IApiKeyService>());
		appBuilder.Services.AddSingleton(Mock.Of<IAuthAuditService>());

		WebApplication app = appBuilder.Build();

		app.UseAuthentication();
		app.UseAuthorization();

		app.MapGet("/secure", () => Results.Ok("authorized")).RequireAuthorization();

		return app;
	}

	private static async Task<HttpResponseMessage> CallSecureAsync(WebApplication app, string token)
	{
		HttpClient client = app.GetTestClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/secure");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		return await client.SendAsync(request);
	}

	[Fact]
	public async Task AuthenticatedRequest_WithMatchingSecurityStamp_Succeeds()
	{
		// Arrange — the live user's stamp equals the stamp baked into the token.
		ApplicationUser user = new() { Id = "user-123", Email = "test@example.com", SecurityStamp = "stamp-current" };
		Mock<UserManager<ApplicationUser>> userManagerMock = CreateUserManagerMock();
		userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);

		await using WebApplication app = BuildHost(userManagerMock.Object);
		await app.StartAsync();

		string token = MintToken("user-123", "stamp-current");

		// Act
		HttpResponseMessage response = await CallSecureAsync(app, token);

		// Assert — the security_stamp and NameIdentifier claims round-tripped through real JwtBearer
		// validation and OnTokenValidated accepted the token.
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task AuthenticatedRequest_AfterSecurityStampRotated_Returns401()
	{
		// Arrange — the user's stamp was rotated (e.g. deactivation/reset) after the token was issued;
		// the token still carries the old stamp.
		ApplicationUser user = new() { Id = "user-123", Email = "test@example.com", SecurityStamp = "stamp-rotated" };
		Mock<UserManager<ApplicationUser>> userManagerMock = CreateUserManagerMock();
		userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);

		await using WebApplication app = BuildHost(userManagerMock.Object);
		await app.StartAsync();

		string token = MintToken("user-123", "stamp-old");

		// Act
		HttpResponseMessage response = await CallSecureAsync(app, token);

		// Assert — a signature-valid, unexpired token is rejected purely because its stamp no longer matches.
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task AuthenticatedRequest_WhenUserIsLockedOut_Returns401_EvenWithMatchingStamp()
	{
		// Arrange — a deactivated/locked account is rejected regardless of the stamp comparison.
		ApplicationUser user = new() { Id = "user-123", Email = "test@example.com", SecurityStamp = "stamp-current" };
		Mock<UserManager<ApplicationUser>> userManagerMock = CreateUserManagerMock();
		userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

		await using WebApplication app = BuildHost(userManagerMock.Object);
		await app.StartAsync();

		string token = MintToken("user-123", "stamp-current");

		// Act
		HttpResponseMessage response = await CallSecureAsync(app, token);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
