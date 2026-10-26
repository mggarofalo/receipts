using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Configuration;
using API.Controllers;
using Application.Classification;
using Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed class CategoryAssistanceHttpAuthorizationTests
{
	private sealed class TestIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			string role = Request.Headers["X-Test-Role"].ToString();
			if (role.Length == 0)
			{
				return Task.FromResult(AuthenticateResult.NoResult());
			}

			var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "test-user"),
				new(ClaimTypes.Role, role)], Scheme.Name);
			return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
		}
	}

	[Fact]
	public async Task AuthenticatedSuggestionsAndAdminOnlySettingsActivityEnforceProductionPolicies()
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration.Sources.Clear();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Jwt:Key"] = new string('k', 40) });
		builder.Services.AddAuthServices(builder.Configuration);
		builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestIdentity>(
			"TestIdentity", _ => { });
		builder.Services.PostConfigure<PolicySchemeOptions>(AuthConfiguration.PolicySchemeName,
			options => options.ForwardDefaultSelector = _ => "TestIdentity");
		builder.Services.AddControllers().AddApplicationPart(typeof(CategoryAssistanceController).Assembly);
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceSettings(false, null, null, 10, 3, Guid.NewGuid()));
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceCapability("jev", CategoryAssistanceCapabilityState.Unavailable, []));
		builder.Services.AddSingleton(settings.Object);
		builder.Services.AddSingleton(capability.Object);
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceReadStore>());
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceReviewPreferenceStore>());
		var decisions = new Mock<ICategoryAssistanceDecisionStore>();
		decisions.Setup(x => x.DismissAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>())).ReturnsAsync((string actor, Guid attempt, Guid operation, CancellationToken ct) =>
				new CategoryAssistanceDecisionResult(attempt, "dismissed", [], null));
		builder.Services.AddSingleton(decisions.Object);
		await using var app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		using var client = app.GetTestClient();

		(await client.GetAsync("/api/category-assistance/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.GetAsync("/api/category-assistance/suggestions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PostAsJsonAsync($"/api/category-assistance/suggestions/{Guid.NewGuid()}/dismiss",
			new CategoryAssistanceDismissRequest(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		(await client.GetAsync("/api/category-assistance/status")).StatusCode.Should().Be(HttpStatusCode.OK);
		(await client.GetAsync("/api/category-assistance/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await client.GetAsync("/api/category-assistance/suggestions")).StatusCode.Should().Be(HttpStatusCode.OK);
		(await client.GetAsync("/api/category-assistance/suggestions?offset=-1")).StatusCode
			.Should().Be(HttpStatusCode.BadRequest);
		(await client.GetAsync("/api/category-assistance/suggestions?limit=101")).StatusCode
			.Should().Be(HttpStatusCode.BadRequest);
		(await client.PostAsJsonAsync($"/api/category-assistance/suggestions/{Guid.NewGuid()}/dismiss",
			new CategoryAssistanceDismissRequest(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.OK);
		(await client.GetAsync("/api/category-assistance/activity")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		client.DefaultRequestHeaders.Remove("X-Test-Role");
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.Admin);
		(await client.GetAsync("/api/category-assistance/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
