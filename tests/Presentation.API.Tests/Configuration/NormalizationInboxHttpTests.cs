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

public sealed class NormalizationInboxHttpTests
{
	[Theory]
	[InlineData(403, "owner_required")]
	[InlineData(409, "preferences_changed")]
	public async Task SettingsRejectWithRfc9457ReasonAndAuthenticatedActor(int status, string code)
	{
		var service = new Mock<INormalizationReviewInboxService>(MockBehavior.Strict);
		var revision = Guid.NewGuid();
		service.Setup(x => x.UpdatePreferencesAsync("owner", new(true, "test-model", 10, revision), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationCatalogException(status, code, "Reload the saved settings."));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PutAsJsonAsync("/api/normalization-inbox/preferences", new { enabled = true, model = "test-model", dailyLimit = 10, expectedRevision = revision, actor = "attacker" });
		((int)response.StatusCode).Should().Be(status);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("detail").GetString().Should().Be("Reload the saved settings.");
		json.RootElement.GetProperty("code").GetString().Should().Be(code);
		service.VerifyAll(); service.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ListPreservesExactSourceAndCalendarReceiptDate()
	{
		var service = new Mock<INormalizationReviewInboxService>(MockBehavior.Strict);
		var receiptId = Guid.NewGuid(); var itemId = Guid.NewGuid();
		service.Setup(x => x.ListAsync("owner", "milk", 25, 25, It.IsAny<CancellationToken>())).ReturnsAsync(new NormalizationReviewInboxPage([
			new("source-a", new("Store A", "MILK", "001"), 2, 1, DateTimeOffset.Parse("2026-10-23T00:00:00Z"), [new(receiptId, itemId, new DateOnly(2020, 2, 29))], "manual", null, null, null, null)
		], 26, 25, 25));
		await using var app = await Start(service);
		using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.GetAsync("/api/normalization-inbox?q=milk&offset=25&limit=25");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var row = json.RootElement.GetProperty("items")[0];
		row.GetProperty("source").GetProperty("itemCode").GetString().Should().Be("001");
		row.GetProperty("receipts")[0].GetProperty("date").GetString().Should().Be("2020-02-29");
		row.GetProperty("receipts")[0].GetProperty("receiptId").GetGuid().Should().Be(receiptId);
		json.RootElement.GetProperty("total").GetInt32().Should().Be(26);
		service.VerifyAll();
	}

	[Fact]
	public async Task AnonymousAndOrdinaryUsersCannotReachInbox()
	{
		var service = new Mock<INormalizationReviewInboxService>(MockBehavior.Strict);
		await using var app = await Start(service);
		using var client = app.GetTestClient();
		(await client.GetAsync("/api/normalization-inbox")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("Test-Role", "User");
		(await client.GetAsync("/api/normalization-inbox")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
		service.VerifyNoOtherCalls();
	}

	private static async Task<WebApplication> Start(Mock<INormalizationReviewInboxService> service)
	{
		var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(NormalizationInboxController).Assembly);
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
