using System.Buffers.Text;
using System.Text;
using FluentAssertions;
using Infrastructure.Ynab.OAuth;
using Microsoft.AspNetCore.DataProtection;

namespace Infrastructure.Tests.Ynab;

public class YnabOAuthTokenProtectorTests
{
	private const string AccessToken = "access-token-a1b2c3";
	private const string RefreshToken = "refresh-token-d4e5f6";

	private readonly EphemeralDataProtectionProvider _provider = new();
	private readonly YnabOAuthTokenProtector _protector;

	public YnabOAuthTokenProtectorTests()
	{
		_protector = new YnabOAuthTokenProtector(_provider);
	}

	[Fact]
	public void TryUnprotect_PayloadFromProtect_ReturnsBothTokens()
	{
		// Arrange
		string payload = _protector.Protect(AccessToken, RefreshToken);

		// Act
		bool readable = _protector.TryUnprotect(payload, out string? accessToken, out string? refreshToken);

		// Assert
		readable.Should().BeTrue();
		accessToken.Should().Be(AccessToken);
		refreshToken.Should().Be(RefreshToken);
	}

	[Fact]
	public void Constructor_WithAProviderFactory_ResolvesTheProviderOnlyOnFirstUseAndOnlyOnce()
	{
		// Arrange — tools that register Infrastructure but never touch YNAB have no Data Protection.
		int resolutions = 0;
		YnabOAuthTokenProtector protector = new(() =>
		{
			resolutions++;
			return _provider;
		});
		int resolutionsAfterConstruction = resolutions;

		// Act
		string payload = protector.Protect(AccessToken, RefreshToken);
		bool readable = protector.TryUnprotect(payload, out string? accessToken, out _);

		// Assert
		resolutionsAfterConstruction.Should().Be(0);
		resolutions.Should().Be(1);
		readable.Should().BeTrue();
		accessToken.Should().Be(AccessToken);
	}

	[Fact]
	public void Protect_Payload_ContainsNeitherTokenInClearText()
	{
		// Act
		string payload = _protector.Protect(AccessToken, RefreshToken);

		// Assert — neither as written, nor underneath the payload's base64url encoding.
		payload.Should().NotContain(AccessToken).And.NotContain(RefreshToken);
		string decoded = Encoding.Latin1.GetString(Base64Url.DecodeFromChars(payload));
		decoded.Should().NotContain(AccessToken).And.NotContain(RefreshToken);
	}

	[Fact]
	public void TryUnprotect_PayloadFromDifferentKeyRing_ReturnsFalseWithoutThrowing()
	{
		// Arrange — what a database moved to another host looks like.
		YnabOAuthTokenProtector otherHost = new(new EphemeralDataProtectionProvider());
		string payload = otherHost.Protect(AccessToken, RefreshToken);
		bool readable = true;
		string? accessToken = "unset";
		string? refreshToken = "unset";

		// Act
		Action act = () => readable = _protector.TryUnprotect(payload, out accessToken, out refreshToken);

		// Assert
		act.Should().NotThrow();
		readable.Should().BeFalse();
		accessToken.Should().BeNull();
		refreshToken.Should().BeNull();
	}

	[Fact]
	public void TryUnprotect_PayloadProtectedForAnotherPurpose_ReturnsFalse()
	{
		// Arrange — same key ring, different purpose: no other protector's payload is accepted.
		string payload = _provider.CreateProtector("Receipts.SomethingElse.v1")
			.Protect($$"""{"AccessToken":"{{AccessToken}}","RefreshToken":"{{RefreshToken}}"}""");

		// Act
		bool readable = _protector.TryUnprotect(payload, out string? accessToken, out string? refreshToken);

		// Assert
		readable.Should().BeFalse();
		accessToken.Should().BeNull();
		refreshToken.Should().BeNull();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not a protected payload")]
	[InlineData("Zm9vYmFyYmF6")]
	[InlineData("%%%")]
	public void TryUnprotect_MissingOrGarbagePayload_ReturnsFalseWithoutThrowing(string? payload)
	{
		// Arrange
		bool readable = true;
		string? accessToken = "unset";
		string? refreshToken = "unset";

		// Act
		Action act = () => readable = _protector.TryUnprotect(payload, out accessToken, out refreshToken);

		// Assert
		act.Should().NotThrow();
		readable.Should().BeFalse();
		accessToken.Should().BeNull();
		refreshToken.Should().BeNull();
	}

	[Theory]
	[InlineData("""{"AccessToken":"a","RefreshToken":""}""")]
	[InlineData("""{"AccessToken":"","RefreshToken":"r"}""")]
	[InlineData("""{"AccessToken":"a"}""")]
	[InlineData("not json")]
	[InlineData("null")]
	public void TryUnprotect_DecryptablePayloadWithoutAUsablePair_ReturnsFalse(string content)
	{
		// Arrange — correctly encrypted for this purpose, but not a complete token pair.
		string payload = _provider.CreateProtector(YnabOAuthTokenProtector.Purpose).Protect(content);

		// Act
		bool readable = _protector.TryUnprotect(payload, out string? accessToken, out string? refreshToken);

		// Assert
		readable.Should().BeFalse();
		accessToken.Should().BeNull();
		refreshToken.Should().BeNull();
	}
}
