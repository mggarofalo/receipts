using API.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.API.Tests.Fixtures;

public class ConfiguredApiTestHostTests
{
	[Fact]
	public void CreateBuilder_UsesOnlyExplicitConfiguration()
	{
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder(
			new Dictionary<string, string?> { ["Test:Marker"] = "explicit" });

		builder.Configuration["Test:Marker"].Should().Be("explicit");
		builder.Configuration["Jwt:Key"].Should().BeNull(
			"the reusable test host must not inherit the runner's Jwt__Key environment variable");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("1234567890123456789012345678901")]
	public void ProductionAuthentication_RejectsMissingOrShortUtf8SigningKey(string? key)
	{
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder(
			new Dictionary<string, string?> { ["Jwt:Key"] = key });

		Action registerProductionAuthentication = () =>
			builder.Services.AddAuthServices(builder.Configuration);

		registerProductionAuthentication.Should().Throw<InvalidOperationException>()
			.WithMessage("*Jwt:Key*");
	}

	[Fact]
	public void ProductionAuthentication_AcceptsSigningKeyThatIs32Utf8Bytes()
	{
		const string key = "123456789012345678901234567890é";
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder(
			new Dictionary<string, string?> { ["Jwt:Key"] = key });

		Action registerProductionAuthentication = () =>
			builder.Services.AddAuthServices(builder.Configuration);

		registerProductionAuthentication.Should().NotThrow(
			"the 30 ASCII bytes plus the two-byte Unicode character form a valid 32-byte key");
	}
}
