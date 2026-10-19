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
	[InlineData("candidates", "GET")]
	[InlineData("runs", "GET")]
	[InlineData("runs", "POST")]
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
	public async Task StartUsesAuthenticatedActorAndSerializesDurableRunContract()
	{
		Guid intent = Guid.NewGuid(), item = Guid.NewGuid(), revision = Guid.NewGuid(), runId = Guid.NewGuid();
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		service.Setup(x => x.StartAsync("owner", intent, It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { item })), "model-a", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new NormalizationAiRun(runId, revision, intent, "queued", "model-a", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, null, null, [], null));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync("/api/normalization-ai/runs", new { clientRequestId = intent, itemIds = new[] { item }, model = "model-a", actor = "attacker" });
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("id").GetGuid().Should().Be(runId);
		json.RootElement.GetProperty("revision").GetGuid().Should().Be(revision);
		json.RootElement.GetProperty("clientRequestId").GetGuid().Should().Be(intent);
		json.RootElement.GetProperty("state").GetString().Should().Be("queued");
		json.RootElement.GetProperty("proposals").GetArrayLength().Should().Be(0);
		service.VerifyAll();
	}

	[Fact]
	public async Task MissingRequiredStartBodyIsRejectedBeforeUseCase()
	{
		var service = new Mock<INormalizationAiService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync("/api/normalization-ai/runs", new { });
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
