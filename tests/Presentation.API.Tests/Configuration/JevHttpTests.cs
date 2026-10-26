using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Jev;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.API.Tests.Fixtures;

namespace Presentation.API.Tests.Configuration;

public class JevHttpTests
{
	[Theory]
	[InlineData("GET", "/api/jev/status", null, 401)]
	[InlineData("POST", "/api/jev/check", null, 401)]
	[InlineData("GET", "/api/jev/status", "User", 403)]
	[InlineData("POST", "/api/jev/check", "User", 403)]
	[InlineData("GET", "/api/jev/status", "Admin", 200)]
	[InlineData("POST", "/api/jev/check", "Admin", 200)]
	public async Task HostedEndpointsEnforceAdminAndMissingKeyNeverCallsProvider(string method, string path, string? role, int expected)
	{
		CountingHandler handler = new(HttpStatusCode.OK);
		await using WebApplication app = await Start(handler);
		using HttpClient client = app.GetTestClient();
		if (role is not null)
		{
			client.DefaultRequestHeaders.Add("Test-Role", role);
		}

		using HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
		((int)response.StatusCode).Should().Be(expected);
		if (expected == 200)
		{
			using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			body.RootElement.GetProperty("state").GetString().Should().Be("missing");
			body.RootElement.GetProperty("models").GetArrayLength().Should().Be(0);
		}
		handler.Calls.Should().Be(0);
	}

	[Theory]
	[InlineData(429, "rateLimited")]
	[InlineData(500, "providerUnavailable")]
	[InlineData(503, "providerUnavailable")]
	public async Task ProductionClientRemainsOneAttemptAfterRepeatedProviderFailures(int status, string reason)
	{
		CountingHandler handler = new((HttpStatusCode)status);
		await using WebApplication app = await Start(handler, "fake-key");
		IClassificationProviderConnection connection = app.Services.GetRequiredService<IClassificationProviderConnection>();
		for (int index = 0; index < 110; index++)
		{
			ProviderConnectionStatus actual = await connection.CheckAsync(default);
			actual.State.Should().Be(ProviderConnectionState.Unavailable);
			actual.Reason.Should().Be(reason);
			handler.Calls.Should().Be(index + 1);
		}
	}

	[Fact]
	public async Task SuggestionInferenceRequiresExplicitOptInAndDoesNotInheritRetries()
	{
		CountingHandler handler = new(HttpStatusCode.TooManyRequests);
		CategorySuggestionRequest request = new("purchase", "revision", [new("category", "Category", "Purchases", null)]);
		await using (WebApplication disabled = await Start(handler, "fake-key"))
		{
			ICategorySuggestionProvider provider = disabled.Services.GetRequiredService<ICategorySuggestionProvider>();
			provider.IsAvailable.Should().BeFalse();
			(await provider.SuggestAsync(request, default)).Failure.Should().Be(SuggestionFailure.Disabled);
			handler.Calls.Should().Be(0);
		}
		await using WebApplication enabled = await Start(handler, "fake-key", true);
		ICategorySuggestionProvider available = enabled.Services.GetRequiredService<ICategorySuggestionProvider>();
		available.IsAvailable.Should().BeTrue();
		for (int index = 0; index < 3; index++)
		{
			(await available.SuggestAsync(request, default)).Failure.Should().Be(SuggestionFailure.RateLimited);
			handler.Calls.Should().Be(index + 1);
		}
	}

	private static async Task<WebApplication> Start(CountingHandler handler, string? key = null, bool inference = false)
	{
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder(new Dictionary<string, string?> { ["TYPESAFE_API_KEY"] = key, ["Jev:InferenceEnabled"] = inference.ToString() });
		builder.AddServiceDefaults();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(JevController).Assembly);
		builder.Services.AddJevServices().ConfigurePrimaryHttpMessageHandler(() => handler);
		builder.Services.AddHttpClient<ICategorySuggestionProvider, JevSuggestionProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
		WebApplication app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(status));
		}
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

			ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
		}
	}
}
