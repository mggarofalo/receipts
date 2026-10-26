using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Configuration;
using API.Controllers;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.NormalizationAi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Configuration;

public class ChatGptConnectionHttpTests
{
	private const string Root = "/api/normalization-ai/connection/";

	[Theory]
	[InlineData("sign-in", "POST")]
	[InlineData("reconnect", "POST")]
	[InlineData("disconnect", "POST")]
	[InlineData("sign-in/10000000-0000-0000-0000-000000000001", "GET")]
	[InlineData("sign-in/10000000-0000-0000-0000-000000000001/cancel", "POST")]
	public async Task AuthenticationRoleAndOwnerAreRequired(string path, string method)
	{
		var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		await using var app = await Start(ceremony); using var client = app.GetTestClient();
		async Task<HttpResponseMessage> Send() => await client.SendAsync(new(new HttpMethod(method), Root + path) { Content = JsonContent.Create(new { ownerConsent = true, actor = "owner" }) });
		using var anonymous = await Send(); anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("Test-Role", "User");
		using var user = await Send(); user.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		client.DefaultRequestHeaders.Remove("Test-Role"); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		client.DefaultRequestHeaders.Add("Test-Actor", "other-admin");
		using var other = await Send(); other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await other.Content.ReadAsStringAsync()).Should().Contain("owner_required"); ceremony.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("sign-in")]
	[InlineData("reconnect")]
	public async Task StartUsesClaimsAndExplicitConsentAndNoStore(string route)
	{
		Guid id = Guid.NewGuid(); var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		ceremony.Setup(x => x.Start("owner", true)).Returns(new ChatGptSignInAttempt(id, "waiting", "https://auth.openai.com/codex/device", "TEST-CODE", DateTimeOffset.UtcNow.AddSeconds(900)));
		await using var app = await Start(ceremony); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsJsonAsync(Root + route, new { ownerConsent = true, actor = "attacker" });
		response.StatusCode.Should().Be(HttpStatusCode.OK); response.Headers.CacheControl!.NoStore.Should().BeTrue();
		response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
		(await response.Content.ReadAsStringAsync()).Should().Contain("localExpiresAt").And.Contain("TEST-CODE").And.NotContain("expiresAt");
		ceremony.VerifyAll(); ceremony.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"ownerConsent\":false}")]
	public async Task MissingOrFalseConsentCannotAuthorize(string body)
	{
		var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		ceremony.Setup(x => x.Start("owner", false)).Throws(new NormalizationAiException(400, "consent_required", "PRIVATE"));
		await using var app = await Start(ceremony); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsync(Root + "sign-in", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest); (await response.Content.ReadAsStringAsync()).Should().NotContain("PRIVATE");
		ceremony.Verify(x => x.Start("owner", true), Times.Never);
	}

	[Fact]
	public async Task StatusCancelAndDisconnectPreserveSeparateSemantics()
	{
		Guid id = Guid.NewGuid(); var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		ceremony.Setup(x => x.Get("owner", id)).Returns(new ChatGptSignInAttempt(id, "restart"));
		ceremony.Setup(x => x.CancelAsync("owner", id, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptSignInAttempt(id, "cancelled", "PRIVATE-URL", "PRIVATE-CODE", DateTimeOffset.UtcNow));
		ceremony.Setup(x => x.DisconnectAsync("owner", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		await using var app = await Start(ceremony); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var status = await client.GetAsync(Root + "sign-in/" + id); (await status.Content.ReadAsStringAsync()).Should().Contain("restart");
		using var cancel = await client.PostAsync(Root + $"sign-in/{id}/cancel", null);
		(await cancel.Content.ReadAsStringAsync()).Should().Contain("cancelled").And.NotContain("PRIVATE");
		ceremony.Verify(x => x.DisconnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		using var disconnect = await client.PostAsync(Root + "disconnect", null); disconnect.StatusCode.Should().Be(HttpStatusCode.NoContent);
		ceremony.VerifyAll(); ceremony.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("operator_busy", 409)]
	[InlineData("PRIVATE-CODE", 503)]
	public async Task ProviderDiagnosticsNeverReachProblems(string code, int expectedStatus)
	{
		var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		ceremony.Setup(x => x.DisconnectAsync("owner", It.IsAny<CancellationToken>())).ThrowsAsync(new ChatGptRunnerException(code));
		await using var app = await Start(ceremony); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.PostAsync(Root + "disconnect", null);
		((int)response.StatusCode).Should().Be(expectedStatus); response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		(await response.Content.ReadAsStringAsync()).Should().NotContain(code);
	}

	[Fact]
	public async Task CompositionSharesSingletonAndIncludesRecovery()
	{
		var services = new ServiceCollection(); services.AddLogging();
		services.Configure<NormalizationAiOptions>(o => { o.Enabled = true; o.OwnerUserId = "owner"; });
		services.Configure<PiNormalizationRunnerOptions>(o => o.HomeDirectory = Path.Combine(Path.GetTempPath(), "unused-chatgpt-http-" + Guid.NewGuid()));
		services.AddSingleton(Mock.Of<IChatGptRunner>()); services.AddScoped(_ => Mock.Of<INormalizationAiService>());
		services.AddChatGptConnectionServices(true);
		await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
		using var first = provider.CreateScope(); using var second = provider.CreateScope();
		first.ServiceProvider.GetRequiredService<IChatGptDeviceSignIn>().Should().BeSameAs(second.ServiceProvider.GetRequiredService<IChatGptDeviceSignIn>());
		provider.GetServices<IHostedService>().Should().ContainSingle(x => x is ChatGptConnectionRecoveryWorker);
	}
	[Fact]
	public async Task RealCoordinatorCancelsWithoutLogoutAndHostDisposalObservesAttempt()
	{
		string home = Path.Combine(Path.GetTempPath(), "receipts-http-device-" + Guid.NewGuid().ToString("N"));
		var runner = new Mock<IChatGptRunner>(MockBehavior.Strict);
		var fence = new Mock<INormalizationAiService>(MockBehavior.Strict);
		Guid generation = Guid.NewGuid();
		fence.Setup(x => x.BeginConnectionChangeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(generation);
		fence.Setup(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		runner.Setup(x => x.LoginAsync(It.IsAny<Action<ChatGptDeviceCode>>(), It.IsAny<CancellationToken>()))
			.Returns(async (Action<ChatGptDeviceCode> notify, CancellationToken token) =>
			{
				notify(new("https://auth.openai.com/codex/device", "FAKE-ONLY", 900));
				await Task.Delay(Timeout.Infinite, token);
			});
		runner.Setup(x => x.LogoutAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		try
		{
			await using var app = await Start(new Mock<IChatGptDeviceSignIn>(), services =>
			{
				services.RemoveAll<IChatGptDeviceSignIn>();
				services.AddSingleton(runner.Object); services.AddScoped(_ => fence.Object);
				services.Configure<PiNormalizationRunnerOptions>(o => o.HomeDirectory = home);
				services.AddChatGptConnectionServices(false);
			});
			using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Role", "Admin");
			using var start = await client.PostAsJsonAsync(Root + "sign-in", new { ownerConsent = true });
			start.StatusCode.Should().Be(HttpStatusCode.OK);
			using var snapshot = System.Text.Json.JsonDocument.Parse(await start.Content.ReadAsStringAsync());
			snapshot.RootElement.GetProperty("state").GetString().Should().Be("waiting");
			Guid id = snapshot.RootElement.GetProperty("id").GetGuid();
			using var cancel = await client.PostAsync(Root + $"sign-in/{id}/cancel", null);
			(await cancel.Content.ReadAsStringAsync()).Should().Contain("cancelled").And.NotContain("FAKE-ONLY");
			runner.Verify(x => x.LogoutAsync(It.IsAny<CancellationToken>()), Times.Never);
			fence.Verify(x => x.BindConnectionChangeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
			using var disconnect = await client.PostAsync(Root + "disconnect", null);
			disconnect.StatusCode.Should().Be(HttpStatusCode.NoContent);
			runner.Verify(x => x.LogoutAsync(It.IsAny<CancellationToken>()), Times.Once);
			// Leave a fresh pending attempt for the container's async singleton disposal.
			using var pending = await client.PostAsJsonAsync(Root + "reconnect", new { ownerConsent = true });
			pending.StatusCode.Should().Be(HttpStatusCode.OK);
		}
		finally
		{
			if (Directory.Exists(home))
			{
				Directory.Delete(home, true);
			}
		}
		fence.Verify(x => x.CompleteConnectionChangeAsync(generation, It.IsAny<CancellationToken>()), Times.Exactly(3));
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData("sign-in", "POST")]
	[InlineData("reconnect", "POST")]
	[InlineData("disconnect", "POST")]
	[InlineData("sign-in/10000000-0000-0000-0000-000000000001", "GET")]
	[InlineData("sign-in/10000000-0000-0000-0000-000000000001/cancel", "POST")]
	public async Task DisabledOrUnownedDeploymentNeverInvokesCeremony(string path, string method)
	{
		foreach (var settings in new[] { (Enabled: false, Owner: "owner"), (Enabled: true, Owner: ""), (Enabled: true, Owner: " ") })
		{
			var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
			await using var app = await Start(ceremony, services => services.Configure<NormalizationAiOptions>(o =>
			{
				o.Enabled = settings.Enabled;
				o.OwnerUserId = settings.Owner;
			}));
			using var client = app.GetTestClient();
			client.DefaultRequestHeaders.Add("Test-Role", "Admin");
			using var response = await client.SendAsync(new(new HttpMethod(method), Root + path)
			{ Content = JsonContent.Create(new { ownerConsent = true }) });
			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
			response.Headers.CacheControl!.NoStore.Should().BeTrue();
			(await response.Content.ReadAsStringAsync()).Should().Contain("not_configured");
			ceremony.VerifyNoOtherCalls();
		}
	}

	[Theory]
	[InlineData("success")]
	[InlineData("cancelled")]
	[InlineData("expired")]
	[InlineData("rejected")]
	[InlineData("busy")]
	[InlineData("reconnect")]
	[InlineData("restart")]
	public async Task PollingEveryTerminalStateClearsCeremonyValues(string state)
	{
		Guid id = Guid.NewGuid();
		var ceremony = new Mock<IChatGptDeviceSignIn>(MockBehavior.Strict);
		ceremony.Setup(x => x.Get("owner", id)).Returns(new ChatGptSignInAttempt(id, state,
			"https://auth.openai.com/codex/device", "PRIVATE-CODE", DateTimeOffset.UtcNow));
		await using var app = await Start(ceremony);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-Role", "Admin");
		using var response = await client.GetAsync(Root + "sign-in/" + id);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.CacheControl!.NoStore.Should().BeTrue();
		using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("state").GetString().Should().Be(state);
		foreach (string property in new[] { "verificationUrl", "userCode", "localExpiresAt" })
		{
			if (body.RootElement.TryGetProperty(property, out var value))
			{
				value.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
			}
		}
		ceremony.VerifyAll();
		ceremony.VerifyNoOtherCalls();
	}

	private static async Task<WebApplication> Start(Mock<IChatGptDeviceSignIn> service, Action<IServiceCollection>? configure = null)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(NormalizationAiController).Assembly);
		builder.Services.AddSingleton(service.Object);
		builder.Services.Configure<NormalizationAiOptions>(o => { o.Enabled = true; o.OwnerUserId = "owner"; });
		configure?.Invoke(builder.Services);
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

			var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Request.Headers["Test-Actor"].FirstOrDefault() ?? "owner"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
		}
	}
}
