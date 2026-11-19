using Common;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Ynab.OAuth;

/// <summary>The YNAB OAuth application this deployment acts as.</summary>
public sealed record YnabOAuthSettings(string ClientId, string ClientSecret, string RedirectUri)
{
	// A record's generated ToString would print the secret.
	public override string ToString() => $"{nameof(YnabOAuthSettings)} {{ ClientId = {ClientId}, RedirectUri = {RedirectUri} }}";
}

public class YnabOAuthSettingsProvider(IConfiguration configuration)
{
	/// <summary>
	/// The client settings, or null when OAuth is not available on this deployment. All three
	/// values are needed, and the redirect address must be absolute because YNAB compares it
	/// exactly with the registered one.
	/// </summary>
	public virtual YnabOAuthSettings? Current
	{
		get
		{
			string? clientId = configuration[ConfigurationVariables.YnabOAuthClientId]?.Trim();
			string? clientSecret = configuration[ConfigurationVariables.YnabOAuthClientSecret]?.Trim();
			string? redirectUri = configuration[ConfigurationVariables.YnabOAuthRedirectUri]?.Trim();

			if (string.IsNullOrEmpty(clientId)
				|| string.IsNullOrEmpty(clientSecret)
				|| string.IsNullOrEmpty(redirectUri)
				|| !Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? parsed)
				|| (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
			{
				return null;
			}

			return new YnabOAuthSettings(clientId, clientSecret, redirectUri);
		}
	}
}
