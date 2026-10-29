using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Configuration;
using API.Controllers;
using Application.Classification;
using Application.Taxonomy;
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
		var discovery = new Mock<IOrganizationDiscoveryReader>();
		discovery.Setup(x => x.GetProgressAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OrganizationDiscoveryProgress(true, "watching", 1, 2, 3, 4, 5, null));
		builder.Services.AddSingleton(discovery.Object);
		builder.Services.AddSingleton(Mock.Of<ICategoryAssistanceReadStore>());
		var reviews = new Mock<ICategoryAssistanceReviewPreferenceStore>();
		var attemptId = Guid.NewGuid();
		var revision = Guid.NewGuid();
		var item = Guid.NewGuid();
		reviews.Setup(x => x.ListSavedAsync("test-user", 2, 5, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceSavedReviewPage(3, 2, 5,
				[new(attemptId, "store", "item", "", "later", revision, [item])]));
		reviews.Setup(x => x.ListSavedAsync("test-user", -1, 25, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new TaxonomyException(400, "Invalid pagination."));
		reviews.Setup(x => x.ListSavedAsync("test-user", 0, 1, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new TaxonomyException(403, "An active receipt writer is required."));
		reviews.Setup(x => x.UpdateSavedAsync("test-user", Guid.Empty, It.IsAny<CategoryAssistanceReviewPreferenceRequest>(),
			It.IsAny<CancellationToken>())).ThrowsAsync(new TaxonomyException(400, "Saved decision ID is required."));
		reviews.Setup(x => x.UpdateSavedAsync("test-user", attemptId, It.IsAny<CategoryAssistanceReviewPreferenceRequest>(),
			It.IsAny<CancellationToken>())).ReturnsAsync((string actor, Guid id, CategoryAssistanceReviewPreferenceRequest request,
				CancellationToken ct) => new CategoryAssistanceReviewPreference(id, request.State, revision, request.ExcludedItemIds));
		var missingId = Guid.NewGuid();
		reviews.Setup(x => x.UpdateSavedAsync("test-user", missingId, It.IsAny<CategoryAssistanceReviewPreferenceRequest>(),
			It.IsAny<CancellationToken>())).ThrowsAsync(new TaxonomyException(404, "Saved decision does not exist."));
		var conflictId = Guid.NewGuid();
		reviews.Setup(x => x.UpdateSavedAsync("test-user", conflictId, It.IsAny<CategoryAssistanceReviewPreferenceRequest>(),
			It.IsAny<CancellationToken>())).ThrowsAsync(new TaxonomyException(409, "Review preferences changed."));
		builder.Services.AddSingleton(reviews.Object);
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
		(await client.GetAsync("/api/category-assistance/discovery")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		discovery.Verify(x => x.GetProgressAsync(It.IsAny<CancellationToken>()), Times.Never);
		(await client.GetAsync("/api/category-assistance/suggestions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.GetAsync("/api/category-assistance/review-preferences")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PutAsJsonAsync($"/api/category-assistance/review-preferences/{attemptId}",
			new CategoryAssistanceReviewPreferenceRequest(Guid.NewGuid(), revision, "active", [])))
			.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		(await client.PostAsJsonAsync($"/api/category-assistance/suggestions/{Guid.NewGuid()}/dismiss",
			new CategoryAssistanceDismissRequest(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.User);
		(await client.GetAsync("/api/category-assistance/status")).StatusCode.Should().Be(HttpStatusCode.OK);
		var discoveryResponse = await client.GetFromJsonAsync<OrganizationDiscoveryStatusResponse>("/api/category-assistance/discovery");
		discoveryResponse!.Discovery.ObservedItems.Should().Be(2);
		discoveryResponse.Capability.Should().Be("unavailable");
		discoveryResponse.AutomaticClassificationAvailable.Should().BeFalse();
		string discoveryJson = await client.GetStringAsync("/api/category-assistance/discovery");
		discoveryJson.Should().NotContain("description").And.NotContain("credential").And.NotContain("itemId").And.NotContain("store");
		(await client.GetAsync("/api/category-assistance/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await client.GetAsync("/api/category-assistance/suggestions")).StatusCode.Should().Be(HttpStatusCode.OK);
		var settingsCalls = settings.Invocations.Count;
		var capabilityCalls = capability.Invocations.Count;
		var page = await client.GetFromJsonAsync<CategoryAssistanceSavedReviewPage>("/api/category-assistance/review-preferences?offset=2&limit=5");
		page!.Items.Should().ContainSingle(x => x.AttemptId == attemptId && x.Location == "store");
		page.Offset.Should().Be(2);
		page.Limit.Should().Be(5);
		var badPage = await client.GetAsync("/api/category-assistance/review-preferences?offset=-1");
		badPage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		badPage.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await badPage.Content.ReadAsStringAsync()).Should().Contain("Invalid pagination.");
		var forbidden = await client.GetAsync("/api/category-assistance/review-preferences?limit=1");
		forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		forbidden.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await forbidden.Content.ReadAsStringAsync()).Should().Contain("An active receipt writer is required.");
		var reviewRequest = new CategoryAssistanceReviewPreferenceRequest(Guid.NewGuid(), revision, "active", [item]);
		var invalidUpdate = await client.PutAsJsonAsync($"/api/category-assistance/review-preferences/{Guid.Empty}", reviewRequest);
		invalidUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		invalidUpdate.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await invalidUpdate.Content.ReadAsStringAsync()).Should().Contain("Saved decision ID is required.");
		var update = await client.PutAsJsonAsync($"/api/category-assistance/review-preferences/{attemptId}", reviewRequest);
		update.StatusCode.Should().Be(HttpStatusCode.OK);
		(await update.Content.ReadFromJsonAsync<CategoryAssistanceReviewPreference>())!.ExcludedItemIds.Should().Equal(item);
		reviews.Verify(x => x.UpdateSavedAsync("test-user", attemptId,
			It.Is<CategoryAssistanceReviewPreferenceRequest>(r => r.OperationId == reviewRequest.OperationId &&
				r.ExpectedRevision == revision && r.State == "active" && r.ExcludedItemIds.SequenceEqual(new[] { item })),
			It.IsAny<CancellationToken>()), Times.Once);
		var missing = await client.PutAsJsonAsync($"/api/category-assistance/review-preferences/{missingId}", reviewRequest);
		missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
		missing.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await missing.Content.ReadAsStringAsync()).Should().Contain("Saved decision does not exist.");
		var conflict = await client.PutAsJsonAsync($"/api/category-assistance/review-preferences/{conflictId}", reviewRequest);
		conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
		conflict.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await conflict.Content.ReadAsStringAsync()).Should().Contain("Review preferences changed.");
		settings.Invocations.Count.Should().Be(settingsCalls);
		capability.Invocations.Count.Should().Be(capabilityCalls);
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
