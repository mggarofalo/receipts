using FluentAssertions;
using Infrastructure.Ynab.OAuth;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Tests.Ynab;

public class YnabOAuthSettingsProviderTests
{
	private const string ClientId = "client-id-1";
	private const string ClientSecret = "client-secret-9f3c";
	private const string RedirectUri = "https://receipts.example/settings/ynab/callback";

	private static YnabOAuthSettingsProvider CreateProvider(string? clientId, string? clientSecret, string? redirectUri)
	{
		Dictionary<string, string?> values = [];
		if (clientId is not null)
		{
			values["YNAB_OAUTH_CLIENT_ID"] = clientId;
		}

		if (clientSecret is not null)
		{
			values["YNAB_OAUTH_CLIENT_SECRET"] = clientSecret;
		}

		if (redirectUri is not null)
		{
			values["YNAB_OAUTH_REDIRECT_URI"] = redirectUri;
		}

		return new YnabOAuthSettingsProvider(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
	}

	[Fact]
	public void Current_AllThreeSet_ReturnsTheSettings()
	{
		// Arrange
		YnabOAuthSettingsProvider provider = CreateProvider(ClientId, ClientSecret, RedirectUri);

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings.Should().NotBeNull();
		settings!.ClientId.Should().Be(ClientId);
		settings.ClientSecret.Should().Be(ClientSecret);
		settings.RedirectUri.Should().Be(RedirectUri);
	}

	[Theory]
	[InlineData(false, true, true)]
	[InlineData(true, false, true)]
	[InlineData(true, true, false)]
	[InlineData(false, false, true)]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(false, false, false)]
	public void Current_AnySettingMissing_ReturnsNull(bool hasClientId, bool hasClientSecret, bool hasRedirectUri)
	{
		// Arrange
		YnabOAuthSettingsProvider provider = CreateProvider(
			hasClientId ? ClientId : null,
			hasClientSecret ? ClientSecret : null,
			hasRedirectUri ? RedirectUri : null);

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings.Should().BeNull();
	}

	[Theory]
	[InlineData("", ClientSecret, RedirectUri)]
	[InlineData("   ", ClientSecret, RedirectUri)]
	[InlineData(ClientId, "", RedirectUri)]
	[InlineData(ClientId, "   ", RedirectUri)]
	[InlineData(ClientId, ClientSecret, "")]
	[InlineData(ClientId, ClientSecret, "   ")]
	public void Current_AnySettingBlank_ReturnsNull(string clientId, string clientSecret, string redirectUri)
	{
		// Arrange
		YnabOAuthSettingsProvider provider = CreateProvider(clientId, clientSecret, redirectUri);

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings.Should().BeNull();
	}

	[Theory]
	[InlineData("/settings/ynab/callback")]
	[InlineData("settings/ynab/callback")]
	[InlineData("receipts.example/settings/ynab/callback")]
	[InlineData("ftp://receipts.example/settings/ynab/callback")]
	[InlineData("receipts://settings/ynab/callback")]
	[InlineData("file:///settings/ynab/callback")]
	public void Current_RedirectUriRelativeOrNotHttp_ReturnsNull(string redirectUri)
	{
		// Arrange
		YnabOAuthSettingsProvider provider = CreateProvider(ClientId, ClientSecret, redirectUri);

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings.Should().BeNull();
	}

	[Theory]
	[InlineData("https://receipts.example/settings/ynab/callback")]
	[InlineData("http://localhost:5173/settings/ynab/callback")]
	public void Current_AbsoluteHttpOrHttpsRedirectUri_IsAccepted(string redirectUri)
	{
		// Arrange
		YnabOAuthSettingsProvider provider = CreateProvider(ClientId, ClientSecret, redirectUri);

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings!.RedirectUri.Should().Be(redirectUri);
	}

	[Fact]
	public void Current_ValuesWithSurroundingWhitespace_AreTrimmed()
	{
		// Arrange — a secret read from a file often ends with a newline.
		YnabOAuthSettingsProvider provider = CreateProvider($" {ClientId} ", $"{ClientSecret}\n", $"{RedirectUri} ");

		// Act
		YnabOAuthSettings? settings = provider.Current;

		// Assert
		settings.Should().Be(new YnabOAuthSettings(ClientId, ClientSecret, RedirectUri));
	}

	[Fact]
	public void Settings_ToString_DoesNotContainTheSecret()
	{
		// Arrange
		YnabOAuthSettings settings = new(ClientId, ClientSecret, RedirectUri);

		// Act
		string text = settings.ToString();

		// Assert
		text.Should().NotContain(ClientSecret);
		text.Should().Contain(ClientId);
	}
}
