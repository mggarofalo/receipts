using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Tests.Services;

public class TokenServiceBranchTests
{
	[Fact]
	public void GenerateAccessToken_WithDefaultConfig_UsesPlaceholderValues()
	{
		// Arrange — no JWT config keys at all
		Dictionary<string, string?> config = new();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
		TokenService service = new(configuration);

		// Act — should not throw, falls back to defaults
		string token = service.GenerateAccessToken("user-1", "a@b.com", new List<string> { "User" }, false, "stamp-1");

		// Assert — token is generated with fallback issuer/audience
		token.Should().NotBeNullOrEmpty();

		// Introspect with same service (same fallback key) to verify roundtrip
		TokenIntrospectionResult result = service.IntrospectAccessToken(token);
		result.Active.Should().BeTrue();
		result.Sub.Should().Be("user-1");
		result.Username.Should().Be("a@b.com");
	}

	[Fact]
	public void IntrospectAccessToken_WithDefaultConfig_UsesPlaceholderValues()
	{
		// Arrange — generate with defaults, introspect with defaults
		Dictionary<string, string?> config = new();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
		TokenService service = new(configuration);

		string token = service.GenerateAccessToken("user-2", "b@c.com", new List<string> { "Admin" }, false, "stamp-2");

		// Act
		TokenIntrospectionResult result = service.IntrospectAccessToken(token);

		// Assert
		result.Active.Should().BeTrue();
		result.Scope.Should().Be("Admin");
	}

	[Fact]
	public void GenerateAccessToken_MustResetPassword_AddsClaimToToken()
	{
		// Arrange
		Dictionary<string, string?> config = new()
		{
			["Jwt:Key"] = "test-signing-key-that-is-at-least-32-chars!!",
			["Jwt:Issuer"] = "test-issuer",
			["Jwt:Audience"] = "test-audience",
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
		TokenService service = new(configuration);

		// Act
		string token = service.GenerateAccessToken("user-3", "c@d.com", new List<string> { "User" }, true, "stamp-3");

		// Assert — token is valid and contains the claim
		TokenIntrospectionResult result = service.IntrospectAccessToken(token);
		result.Active.Should().BeTrue();
	}

	[Fact]
	public void GenerateAccessToken_NotMustResetPassword_OmitsClaim()
	{
		// Arrange
		Dictionary<string, string?> config = new()
		{
			["Jwt:Key"] = "test-signing-key-that-is-at-least-32-chars!!",
			["Jwt:Issuer"] = "test-issuer",
			["Jwt:Audience"] = "test-audience",
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
		TokenService service = new(configuration);

		// Act
		string token = service.GenerateAccessToken("user-4", "d@e.com", new List<string> { "User" }, false, "stamp-4");

		// Assert
		TokenIntrospectionResult result = service.IntrospectAccessToken(token);
		result.Active.Should().BeTrue();
	}

	[Fact]
	public void IntrospectAccessToken_MismatchedFallbackVsExplicit_ReturnsInactive()
	{
		// Arrange — generate with explicit config, introspect with defaults (different key)
		Dictionary<string, string?> explicitConfig = new()
		{
			["Jwt:Key"] = "explicit-key-that-is-at-least-32-characters!",
			["Jwt:Issuer"] = "custom-issuer",
			["Jwt:Audience"] = "custom-audience",
		};
		IConfiguration explicitConfiguration = new ConfigurationBuilder()
			.AddInMemoryCollection(explicitConfig)
			.Build();
		TokenService explicitService = new(explicitConfiguration);

		Dictionary<string, string?> defaultConfig = new();
		IConfiguration defaultConfiguration = new ConfigurationBuilder()
			.AddInMemoryCollection(defaultConfig)
			.Build();
		TokenService defaultService = new(defaultConfiguration);

		string token = explicitService.GenerateAccessToken("user-5", "e@f.com", new List<string> { "User" }, false, "stamp-5");

		// Act — introspect with different (default) key
		TokenIntrospectionResult result = defaultService.IntrospectAccessToken(token);

		// Assert
		result.Active.Should().BeFalse();
	}

	[Fact]
	public void GenerateRefreshToken_ReturnsBase64String()
	{
		// Arrange
		Dictionary<string, string?> config = new();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
		TokenService service = new(configuration);

		// Act
		string refreshToken = service.GenerateRefreshToken();

		// Assert
		refreshToken.Should().NotBeNullOrEmpty();
		byte[] decoded = Convert.FromBase64String(refreshToken);
		decoded.Should().HaveCount(64);
	}
}
