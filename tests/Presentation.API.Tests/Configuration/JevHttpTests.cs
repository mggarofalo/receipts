using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Jev;
using Infrastructure.Services;
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
	public async Task AssistanceInferenceHonoursTheServerSwitchAndDoesNotInheritRetries()
	{
		CountingHandler handler = new(HttpStatusCode.TooManyRequests);
		CategorySuggestionRequest request = new("purchase", "revision", [new("category", "Category", "Purchases", null)]);
		await using (WebApplication disabled = await Start(handler, "fake-key", enabled: false))
		{
			await using AsyncServiceScope scope = disabled.Services.CreateAsyncScope();
			ICategoryAssistanceSelectedProvider provider = scope.ServiceProvider.GetRequiredService<ICategoryAssistanceSelectedProvider>();
			provider.IsAvailable.Should().BeFalse();
			(await provider.SuggestAsync(request, Guid.NewGuid(), default)).Failure.Should().Be(SuggestionFailure.Disabled);
			handler.Calls.Should().Be(0);
		}

		await using WebApplication enabled = await Start(handler, "fake-key");
		for (int index = 0; index < 3; index++)
		{
			await using AsyncServiceScope scope = enabled.Services.CreateAsyncScope();
			ICategoryAssistanceSelectedProvider available = scope.ServiceProvider.GetRequiredService<ICategoryAssistanceSelectedProvider>();
			available.IsAvailable.Should().BeTrue();
			(await available.SuggestAsync(request, Guid.NewGuid(), default)).Failure.Should().Be(SuggestionFailure.RateLimited);
			handler.Calls.Should().Be(index + 1);
		}
	}

	[Theory]
	[InlineData("0.5")]
	[InlineData("1.5")]
	public async Task InvalidAutoWriteThresholdStopsStartup(string threshold)
	{
		Func<Task> start = () => Start(new CountingHandler(HttpStatusCode.OK), "fake-key",
			settings: new() { ["CategoryAssistance:RootAutoWriteThreshold"] = threshold });
		await start.Should().ThrowAsync<OptionsValidationException>();
	}

	[Fact]
	public async Task AutoWriteOptionsBindFromCategoryAssistanceSection()
	{
		await using WebApplication app = await Start(new CountingHandler(HttpStatusCode.OK), "fake-key", settings: new()
		{
			["CategoryAssistance:RootAutoWriteThreshold"] = "0.95",
			["CategoryAssistance:LeafAutoWriteEnabled"] = "true"
		});
		CategoryAutoWriteOptions options = app.Services.GetRequiredService<IOptionsMonitor<CategoryAutoWriteOptions>>().CurrentValue;
		options.RootAutoWriteThreshold.Should().Be(0.95);
		options.LeafAutoWriteThreshold.Should().Be(0.90);
		options.LeafAutoWriteEnabled.Should().BeTrue();
	}

	private static async Task<WebApplication> Start(CountingHandler handler, string? key = null, bool enabled = true,
		Dictionary<string, string?>? settings = null)
	{
		Dictionary<string, string?> configuration = new() { ["TYPESAFE_API_KEY"] = key, ["Jev:Enabled"] = enabled.ToString() };
		foreach ((string name, string? value) in settings ?? [])
		{
			configuration[name] = value;
		}

		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder(configuration);
		builder.AddServiceDefaults();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(JevController).Assembly);
		builder.Services.AddJevServices().ConfigurePrimaryHttpMessageHandler(() => handler);
		builder.Services.AddHttpClient<JevSuggestionProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
		// The persisted connection binding needs a database; a current binding isolates the transport under test.
		builder.Services.AddScoped<ICategoryAssistanceConnectionBinding, CurrentBinding>();
		WebApplication app = builder.Build();
		app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private sealed class CurrentBinding : ICategoryAssistanceConnectionBinding
	{
		public Task<bool> IsCurrentAsync(CategoryAssistanceConnectionCoordinator.Configuration configuration, Guid generation,
			CancellationToken ct = default) => Task.FromResult(true);
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
