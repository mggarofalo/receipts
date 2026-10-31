using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Application.NormalizationAi;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class NormalizationAiHttpTests
{
	[Theory]
	[InlineData("connection", "GET")]
	[InlineData("runs", "GET")]
	[InlineData("source-runs", "POST")]
	[InlineData("runs/10000000-0000-0000-0000-000000000001/apply", "POST")]
	public async Task AnonymousAndNonAdminCannotReachService(string path, string method)
	{
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = app.GetTestClient();
		using var anonymous = await client.SendAsync(new(new HttpMethod(method), "/api/normalization-ai/" + path));
		anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("Test-Role", "User");
		using var ordinary = await client.SendAsync(new(new HttpMethod(method), "/api/normalization-ai/" + path));
		ordinary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		service.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(400, "invalid_selection")]
	[InlineData(403, "owner_required")]
	[InlineData(404, "not_found")]
	[InlineData(409, "source_changed")]
	[InlineData(503, "not_configured")]
	public async Task ReasonedRejectionPreservesStatusAndHumanDetail(int status, string code)
	{
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.GetConnectionAsync("owner", It.IsAny<CancellationToken>())).ThrowsAsync(new NormalizationAiException(status, code, "Specific actionable reason"));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.GetAsync("/api/normalization-ai/connection");
		((int)response.StatusCode).Should().Be(status);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("status").GetInt32().Should().Be(status);
		json.RootElement.GetProperty("detail").GetString().Should().Be("Specific actionable reason");
		if (status is 403 or 409 or 503)
		{
			json.RootElement.GetProperty("code").GetString().Should().Be(code);
		}
	}

	[Fact]
	public async Task GetRunUsesAuthenticatedActorAndSerializesDurableRunContract()
	{
		Guid intent = Guid.NewGuid(), revision = Guid.NewGuid(), runId = Guid.NewGuid();
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.GetRunAsync("owner", runId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new NormalizationAiRun(runId, revision, intent, "queued", "model-a", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, null, null, [], null));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.GetAsync($"/api/normalization-ai/runs/{runId}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("id").GetGuid().Should().Be(runId);
		json.RootElement.GetProperty("revision").GetGuid().Should().Be(revision);
		json.RootElement.GetProperty("clientRequestId").GetGuid().Should().Be(intent);
		json.RootElement.GetProperty("state").GetString().Should().Be("queued");
		json.RootElement.GetProperty("proposals").GetArrayLength().Should().Be(0);
		service.VerifyAll();
	}

	[Theory]
	[InlineData("candidates", "GET", HttpStatusCode.NotFound)]
	[InlineData("runs", "POST", HttpStatusCode.MethodNotAllowed)]
	public async Task RetiredPerPurchaseRoutesNoLongerReachService(string path, string method, HttpStatusCode expected)
	{
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.SendAsync(new(new HttpMethod(method), "/api/normalization-ai/" + path)
		{ Content = JsonContent.Create(new { clientRequestId = Guid.NewGuid(), itemIds = new[] { Guid.NewGuid() }, model = "model-a" }) });
		response.StatusCode.Should().Be(expected);
		service.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ApplyingRetiredPerPurchaseRunAnswersConflictProblemWithReason()
	{
		Guid runId = Guid.NewGuid(), proposal = Guid.NewGuid(), revision = Guid.NewGuid();
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.ApplyAsync("owner", runId, It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { proposal })), revision, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationAiException(409, "workflow_retired", "Per-purchase ChatGPT suggestions are retired."));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync($"/api/normalization-ai/runs/{runId}/apply", new { proposalIds = new[] { proposal }, expectedRevision = revision });
		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("code").GetString().Should().Be("workflow_retired");
		json.RootElement.GetProperty("detail").GetString().Should().Be("Per-purchase ChatGPT suggestions are retired.");
		service.VerifyAll();
	}

	[Fact]
	public async Task SourceRunPreservesIndependentScopeConsentAndAuthenticatedActor()
	{
		Guid intent = Guid.NewGuid(), runId = Guid.NewGuid();
		NormalizationAiSourceSelection[] selections = [
			new(new("Store A", "FRANKS", "001"), false),
			new(new("Store B", "FRANKS", null), true)];
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.StartSourcesAsync("owner", intent,
			It.Is<IReadOnlyList<NormalizationAiSourceSelection>>(values => values.SequenceEqual(selections)),
			"model-a", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new NormalizationAiRun(runId, Guid.NewGuid(), intent, "queued", "model-a",
				DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, null, null, [], null));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync("/api/normalization-ai/source-runs",
			new { clientRequestId = intent, sources = selections, model = "model-a", actor = "attacker" });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("id").GetGuid().Should().Be(runId);
		service.VerifyAll();
	}

	[Fact]
	public async Task PendingIdentityConflictPreservesExactRecoveryTargetAndAuthenticatedActor()
	{
		Guid target = Guid.NewGuid();
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.StartSourcesAsync("owner", It.IsAny<Guid>(), It.Is<IReadOnlyList<NormalizationAiSourceSelection>>(values => values.Count == 1 && values[0].LabelOnly), "model-a", It.IsAny<CancellationToken>()))
				.ThrowsAsync(new NormalizationAiException(409, "pending_identity_requires_review", "Explicitly approve the pending identity first.",
						new Dictionary<string, object?> { ["normalizedDescriptionId"] = target, ["normalizedDescriptionStatus"] = "pendingReview" }));
		await using var app = await Start(service);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync("/api/normalization-ai/source-runs", new { clientRequestId = Guid.NewGuid(), sources = new[] { new { source = new { location = "Store A", description = "SOURCE A" }, includeLegacyUnknown = false, labelOnly = true } }, model = "model-a", actor = "attacker" });
		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("status").GetInt32().Should().Be(409);
		json.RootElement.GetProperty("detail").GetString().Should().Be("Explicitly approve the pending identity first.");
		json.RootElement.GetProperty("code").GetString().Should().Be("pending_identity_requires_review");
		json.RootElement.GetProperty("normalizedDescriptionId").GetGuid().Should().Be(target);
		json.RootElement.GetProperty("normalizedDescriptionStatus").GetString().Should().Be("pendingReview");
		service.VerifyAll();
		service.VerifyNoOtherCalls();
	}
	private static async Task<WebApplication> Start(Mock<INormalizationAiService> service)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(NormalizationAiController).Assembly);
		builder.Services.AddSingleton(service.Object);
		var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync(); return app;
	}

	private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.TryGetValue("Test-Role", out var role))
			{
				return Task.FromResult(AuthenticateResult.NoResult());
			}

			var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
		}
	}
}
