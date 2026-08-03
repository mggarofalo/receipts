using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FluentAssertions;
using Xunit;

namespace Receipts.AppHost.Tests;

/// <summary>
/// Guards the frontend resource's endpoint shape.
///
/// <para>
/// RECEIPTS-882: the AppHost used to call <c>WithHttpEndpoint(port: 5173, name: "vite")</c>
/// on top of <c>AddViteApp</c>, which already creates an "http" endpoint. The extra endpoint
/// got its own DCP proxy on 5173 with nothing listening behind it, so every request to
/// <c>http://localhost:5173</c> hung, and the app was only reachable on a random port that
/// changed each run. These tests fail if a second endpoint is reintroduced.
/// </para>
/// </summary>
public class FrontendEndpointTests
{
	private static async Task<EndpointAnnotation[]> GetFrontendEndpointsAsync()
	{
		await using IDistributedApplicationTestingBuilder appHost =
			await DistributedApplicationTestingBuilder.CreateAsync<Projects.Receipts_AppHost>();

		IResource frontend = appHost.Resources.Single(resource => resource.Name == "frontend");

		return [.. frontend.Annotations.OfType<EndpointAnnotation>()];
	}

	[Fact]
	public async Task Frontend_HasExactlyOneEndpoint()
	{
		EndpointAnnotation[] endpoints = await GetFrontendEndpointsAsync();

		// A second endpoint is never served by Vite — Aspire only passes --port for one of
		// them, leaving the other's proxy to accept connections and then hang.
		endpoints.Select(endpoint => endpoint.Name).Should().ContainSingle(
			"AddViteApp already declares the frontend's endpoint; adding another creates a proxy with no listener");
	}

	[Fact]
	public async Task Frontend_EndpointIsNamedHttp()
	{
		EndpointAnnotation[] endpoints = await GetFrontendEndpointsAsync();

		endpoints.Should().AllSatisfy(endpoint =>
		{
			endpoint.Name.Should().Be("http");
			endpoint.UriScheme.Should().Be("http");
		});
	}

	[Fact]
	public async Task Frontend_EndpointIsPinnedTo5173()
	{
		EndpointAnnotation[] endpoints = await GetFrontendEndpointsAsync();

		// docs/visual-regression.md, playwright.config.ts and the QA skills all assume 5173.
		endpoints.Select(endpoint => endpoint.Port).Should().AllBeEquivalentTo(5173);
	}
}
