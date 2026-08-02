using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Presentation.API.Tests;

[Trait("Category", "Integration")]
public class HealthEndpointTests
{
	private const string BackgroundCheckName = "background_worker";

	[Fact]
	public async Task HealthEndpoint_InProduction_ReturnsHealthy_WithStatusOnlyBody()
	{
		using IHost host = CreateHost(Environments.Production, healthyCheck: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response = await client.GetAsync("/health");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		string body = await response.Content.ReadAsStringAsync();
		body.Should().Be(HealthStatus.Healthy.ToString(),
			"production response writer must emit status only (no per-check payload).");
		response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
	}

	[Fact]
	public async Task HealthEndpoint_InProduction_ReturnsUnhealthy_WhenCheckFails()
	{
		using IHost host = CreateHost(Environments.Production, healthyCheck: false);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response = await client.GetAsync("/health");

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		string body = await response.Content.ReadAsStringAsync();
		body.Should().Be(HealthStatus.Unhealthy.ToString());
	}

	[Fact]
	public async Task HealthEndpoint_InDevelopment_ReturnsHealthy_WithAggregatedBody()
	{
		using IHost host = CreateHost(Environments.Development, healthyCheck: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response = await client.GetAsync("/health");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		string body = await response.Content.ReadAsStringAsync();
		body.Should().Be(HealthStatus.Healthy.ToString(),
			"the default Dev response writer still emits the overall status in the body.");
	}

	[Fact]
	public async Task AliveEndpoint_InProduction_ReturnsHealthy()
	{
		using IHost host = CreateHost(Environments.Production, healthyCheck: true);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		// The aliveness endpoint only evaluates checks tagged "live" (the default "self" check),
		// so it should succeed regardless of the state of any "background"-tagged check.
		HttpResponseMessage response = await client.GetAsync("/alive");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task HealthEndpoint_InProduction_IncludesBackgroundTaggedCheck()
	{
		// Regression guard: "background"-tagged checks must flow through to /health so ops
		// has a machine-readable signal for background-worker state.
		using IHost host = CreateHost(Environments.Production, healthyCheck: false);
		await host.StartAsync();
		HttpClient client = host.GetTestClient();

		HttpResponseMessage response = await client.GetAsync("/health");

		// Failure of the background check must affect the aggregate status.
		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
			"'background'-tagged checks must be included in /health aggregation.");
	}

	private static IHost CreateHost(string environmentName, bool healthyCheck)
	{
		WebApplicationBuilder appBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = environmentName,
		});
		appBuilder.WebHost.UseTestServer();

		appBuilder.Services.AddHealthChecks()
			.AddCheck(
				"self",
				() => HealthCheckResult.Healthy(),
				["live"])
			.AddCheck(
				BackgroundCheckName,
				() => healthyCheck ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("forced"),
				["background"]);

		WebApplication app = appBuilder.Build();

		// Exercise the code under test.
		app.MapDefaultEndpoints();

		return app;
	}
}
