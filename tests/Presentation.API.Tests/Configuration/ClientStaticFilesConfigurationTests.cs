using System.Net;
using System.Net.Http.Headers;
using API.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Presentation.API.Tests.Configuration;

// Serves a stand-in client build through the same wiring Program uses in production (RECEIPTS-1088).
public sealed class ClientStaticFilesConfigurationTests : IAsyncLifetime
{
	private readonly string _webRoot = Directory.CreateTempSubdirectory("receipts-client-").FullName;
	private WebApplication _app = null!;
	private HttpClient _client = null!;

	public async Task InitializeAsync()
	{
		File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Receipts</title>");
		File.WriteAllText(Path.Combine(_webRoot, "vite.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
		Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
		File.WriteAllText(Path.Combine(_webRoot, "assets", "index-3f9a1c2b.js"), "console.log('client');");

		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Production,
			WebRootPath = _webRoot,
		});
		builder.WebHost.UseTestServer();
		_app = builder.Build();
		_app.UseClientStaticFiles();
		_app.MapClientFallback();
		await _app.StartAsync();
		_client = _app.GetTestClient();
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _app.DisposeAsync();
		Directory.Delete(_webRoot, recursive: true);
	}

	[Theory]
	[InlineData("/")]
	[InlineData("/index.html")]
	[InlineData("/receipts/5c1d")]
	[InlineData("/assets/stale")]
	public async Task IndexHtml_IsRevalidatedOnEveryRequest(string path)
	{
		// Act
		using HttpResponseMessage response = await _client.GetAsync(path);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
		response.Headers.CacheControl.Should().Be(CacheControlHeaderValue.Parse(ClientStaticFilesConfiguration.Revalidate));
		response.Headers.ETag.Should().NotBeNull("revalidation needs a validator to answer 304");
	}

	[Fact]
	public async Task HashedAssets_AreCachedAsImmutable()
	{
		// Act
		using HttpResponseMessage response = await _client.GetAsync("/assets/index-3f9a1c2b.js");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.CacheControl!.Public.Should().BeTrue();
		response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
		response.Headers.CacheControl.Extensions.Should().ContainSingle(extension => extension.Name == "immutable");
	}

	[Fact]
	public async Task UnhashedFiles_OutsideAssets_AreRevalidated()
	{
		// Act
		using HttpResponseMessage response = await _client.GetAsync("/vite.svg");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.CacheControl!.NoCache.Should().BeTrue();
	}
}
