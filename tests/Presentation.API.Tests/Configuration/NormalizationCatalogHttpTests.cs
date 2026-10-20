using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Application.Normalization;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Configuration;

public sealed class NormalizationCatalogHttpTests
{
	[Theory]
	[InlineData("sources", "GET")]
	[InlineData("coverage", "GET")]
	[InlineData("targets/10000000-0000-0000-0000-000000000001/preview-label", "POST")]
	[InlineData("targets/10000000-0000-0000-0000-000000000001/approve-label", "POST")]
	[InlineData("candidates", "GET")]
	[InlineData("mappings", "GET")]
	[InlineData("operations", "GET")]
	[InlineData("operations", "POST")]
	[InlineData("operations/10000000-0000-0000-0000-000000000001/approve", "POST")]
	[InlineData("operations/10000000-0000-0000-0000-000000000001/resume", "POST")]
	[InlineData("operations/10000000-0000-0000-0000-000000000001/cancel", "POST")]
	[InlineData("operations/10000000-0000-0000-0000-000000000001/undo", "POST")]
	[InlineData("mappings/10000000-0000-0000-0000-000000000001/revoke", "POST")]
	public async Task AnonymousAndOrdinaryUsersCannotReachCatalog(string path, string method)
	{
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = app.GetTestClient();
		using var anonymous = await client.SendAsync(new(new HttpMethod(method), "/api/normalization-catalog/" + path));
		anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("Test-Role", "User");
		using var ordinary = await client.SendAsync(new(new HttpMethod(method), "/api/normalization-catalog/" + path));
		ordinary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		service.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(400, "invalid_source")]
	[InlineData(403, "owner_required")]
	[InlineData(404, "not_found")]
	[InlineData(409, "source_changed")]
	public async Task ReasonedRejectionsUseProblemDetails(int status, string code)
	{
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		service.Setup(x => x.GetCoverageAsync("owner", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationCatalogException(status, code, "Create a new preview."));
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.GetAsync("/api/normalization-catalog/coverage");
		((int)response.StatusCode).Should().Be(status);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("status").GetInt32().Should().Be(status);
		json.RootElement.GetProperty("detail").GetString().Should().Be("Create a new preview.");
		if (status is 403 or 409)
		{
			json.RootElement.GetProperty("code").GetString().Should().Be(code);
		}

		service.VerifyAll();
	}

	[Fact]
	public async Task PreviewPreservesScopeConsentAndSuggestionRevisionWithoutAcceptingActorFromBody()
	{
		Guid requestId = Guid.NewGuid(), target = Guid.NewGuid(), targetRevision = Guid.NewGuid();
		Guid run = Guid.NewGuid(), proposal = Guid.NewGuid(), runRevision = Guid.NewGuid();
		var source = new NormalizationSourceKey("Store A", "FRANKS", "001");
		var expected = new NormalizationCatalogPreview(requestId, source, target, targetRevision, null, true, false, run, proposal, runRevision);
		var operation = Operation(source);
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		service.Setup(x => x.PreviewAsync("owner", expected, It.IsAny<CancellationToken>())).ReturnsAsync(operation);
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsJsonAsync("/api/normalization-catalog/operations", new
		{
			clientRequestId = requestId,
			source,
			targetId = target,
			targetRevision,
			includeLegacyUnknown = true,
			exclude = false,
			suggestionRunId = run,
			suggestionProposalId = proposal,
			suggestionRunRevision = runRevision,
			actor = "attacker",
		});
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("id").GetGuid().Should().Be(operation.Id);
		json.RootElement.GetProperty("revision").GetGuid().Should().Be(operation.Revision);
		json.RootElement.GetProperty("state").GetString().Should().Be("preview");
		json.RootElement.GetProperty("source").GetProperty("itemCode").GetString().Should().Be("001");
		json.RootElement.GetProperty("protected").GetInt32().Should().Be(2);
		json.RootElement.GetProperty("remaining").GetInt32().Should().Be(3);
		service.VerifyAll();
	}

	[Theory]
	[InlineData("approve")]
	[InlineData("cancel")]
	[InlineData("undo")]
	public async Task MutationsForwardExpectedRevisionToAuthoritativeUseCase(string action)
	{
		Guid id = Guid.NewGuid(), revision = Guid.NewGuid();
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		var operation = Operation(new("Store", "Milk", null));
		if (action == "approve")
		{
			service.Setup(x => x.ApproveAsync("owner", id, revision, It.IsAny<CancellationToken>())).ReturnsAsync(operation);
		}

		if (action == "cancel")
		{
			service.Setup(x => x.CancelAsync("owner", id, revision, It.IsAny<CancellationToken>())).ReturnsAsync(operation);
		}

		if (action == "undo")
		{
			service.Setup(x => x.UndoAsync("owner", id, revision, It.IsAny<CancellationToken>())).ReturnsAsync(operation);
		}

		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsJsonAsync($"/api/normalization-catalog/operations/{id}/{action}", new { expectedRevision = revision });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		service.VerifyAll();
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"source\":null}")]
	public async Task MissingPreviewBodyFieldsAreRejectedBeforeService(string body)
	{
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsync("/api/normalization-catalog/operations", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		service.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task LabelPreviewSerializesGlobalImpactWithoutInvokingApproval()
	{
		Guid target = Guid.NewGuid(), revision = Guid.NewGuid();
		var review = new NormalizationCatalogLabelReview(target, revision, "BF FRANKS", null, "Beef franks", 12);
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		service.Setup(x => x.PreviewLabelAsync("owner", target, "Beef franks", It.IsAny<CancellationToken>())).ReturnsAsync(review);
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsJsonAsync($"/api/normalization-catalog/targets/{target}/preview-label", new { displayLabel = "Beef franks" });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("targetRevision").GetGuid().Should().Be(revision);
		json.RootElement.GetProperty("canonicalName").GetString().Should().Be("BF FRANKS");
		json.RootElement.GetProperty("proposedDisplayLabel").GetString().Should().Be("Beef franks");
		json.RootElement.GetProperty("affectedItemCount").GetInt32().Should().Be(12);
		service.VerifyAll();
		service.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LabelApprovalForwardsReviewedImpactRevisionAndOptionalSuggestionReferences(bool suggestion)
	{
		Guid target = Guid.NewGuid(), revision = Guid.NewGuid();
		Guid? run = suggestion ? Guid.NewGuid() : null, proposal = suggestion ? Guid.NewGuid() : null, runRevision = suggestion ? Guid.NewGuid() : null;
		var request = new NormalizationCatalogLabelApproval("Beef franks", revision, 12, run, proposal, runRevision);
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		service.Setup(x => x.ApproveLabelAsync("owner", target, request, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new NormalizationCatalogLabelReview(target, Guid.NewGuid(), "BF FRANKS", "Beef franks", "Beef franks", 12));
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsJsonAsync($"/api/normalization-catalog/targets/{target}/approve-label", request);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		service.VerifyAll();
		service.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task StaleLabelApprovalReturnsActionableProblemAndMachineCode()
	{
		Guid target = Guid.NewGuid(), revision = Guid.NewGuid();
		var request = new NormalizationCatalogLabelApproval("Beef franks", revision, 12);
		var service = new Mock<INormalizationCatalogService>(MockBehavior.Strict);
		service.Setup(x => x.ApproveLabelAsync("owner", target, request, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationCatalogException(409, "label_impact_changed", "Review the changed purchase count."));
		await using var app = await Start(service);
		using var client = Admin(app);
		using var response = await client.PostAsJsonAsync($"/api/normalization-catalog/targets/{target}/approve-label", request);
		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("detail").GetString().Should().Be("Review the changed purchase count.");
		json.RootElement.GetProperty("code").GetString().Should().Be("label_impact_changed");
		service.VerifyAll();
	}
	private static NormalizationCatalogOperation Operation(NormalizationSourceKey source) =>
		new(Guid.NewGuid(), Guid.NewGuid(), "preview", source, null, null, "Franks", true,
			5, 3, 2, 0, 0, 3, 0, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, false);
	private static HttpClient Admin(WebApplication app)
	{
		var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin"); return client;
	}
	private static async Task<WebApplication> Start(Mock<INormalizationCatalogService> service)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(NormalizationCatalogController).Assembly);
		builder.Services.AddSingleton(service.Object);
		var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync(); return app;
	}
	private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
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

