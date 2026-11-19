using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Infrastructure.Ynab.OAuth;

/// <summary>Encrypts the YNAB token pair for storage, under a purpose no other protector shares.</summary>
/// <remarks>
/// The provider is resolved on first use. Infrastructure is also registered by short-lived
/// tools that never touch YNAB and have no Data Protection; only the API configures it.
/// </remarks>
public class YnabOAuthTokenProtector(Func<IDataProtectionProvider> dataProtectionProvider)
{
	public const string Purpose = "Receipts.Ynab.OAuthTokens.v1";

	private readonly Lazy<IDataProtector> _lazyProtector = new(() => dataProtectionProvider().CreateProtector(Purpose));

	public YnabOAuthTokenProtector(IDataProtectionProvider dataProtectionProvider)
		: this(() => dataProtectionProvider)
	{
	}

	private IDataProtector Protector => _lazyProtector.Value;

	public string Protect(string accessToken, string refreshToken) =>
		Protector.Protect(JsonSerializer.Serialize(new Payload(accessToken, refreshToken)));

	/// <summary>
	/// Returns false, without throwing, when the payload is missing or cannot be read — for
	/// example after the database moved to a host with a different key ring.
	/// </summary>
	public bool TryUnprotect(
		string? protectedTokens,
		[NotNullWhen(true)] out string? accessToken,
		[NotNullWhen(true)] out string? refreshToken)
	{
		accessToken = null;
		refreshToken = null;
		if (string.IsNullOrEmpty(protectedTokens))
		{
			return false;
		}

		try
		{
			Payload? payload = JsonSerializer.Deserialize<Payload>(Protector.Unprotect(protectedTokens));
			if (string.IsNullOrEmpty(payload?.AccessToken) || string.IsNullOrEmpty(payload.RefreshToken))
			{
				return false;
			}

			accessToken = payload.AccessToken;
			refreshToken = payload.RefreshToken;
			return true;
		}
		catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
		{
			return false;
		}
	}

	private sealed record Payload(string AccessToken, string RefreshToken);
}
