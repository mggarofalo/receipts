using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Presentation.API.Tests.Fixtures;

/// <summary>
/// Creates a minimal API host whose configuration contains only values explicitly supplied by the
/// test. This prevents policy-level tests from accidentally consuming appsettings or environment
/// values from the test runner while retaining the production service and middleware extensions.
/// </summary>
public static class ConfiguredApiTestHost
{
	public static WebApplicationBuilder CreateBuilder(
		IEnumerable<KeyValuePair<string, string?>> configuration)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration.Sources.Clear();
		builder.Configuration.AddInMemoryCollection(configuration);
		return builder;
	}
}
