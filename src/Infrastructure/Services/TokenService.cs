using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Services;

public class TokenService(IConfiguration configuration) : ITokenService
{
	public string GenerateAccessToken(string userId, string email, IList<string> roles, bool mustResetPassword, string securityStamp)
	{
		string key = configuration[ConfigurationVariables.JwtKey] ?? "build-time-placeholder-key-32-chars!!";
		string issuer = configuration[ConfigurationVariables.JwtIssuer] ?? "receipts-api";
		string audience = configuration[ConfigurationVariables.JwtAudience] ?? "receipts-app";

		SymmetricSecurityKey signingKey = new(Encoding.UTF8.GetBytes(key));
		SigningCredentials credentials = new(signingKey, SecurityAlgorithms.HmacSha256);

		List<Claim> claims =
		[
			new Claim(ClaimTypes.NameIdentifier, userId),
			new Claim(ClaimTypes.Email, email),
			new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
			// Bakes the user's current security stamp into the token so every request can re-check it
			// against the live value and reject sessions that outlived a deactivation or password reset.
			new Claim(AuthClaimTypes.SecurityStamp, securityStamp),
			.. roles.Select(r => new Claim(ClaimTypes.Role, r)),
		];

		if (mustResetPassword)
		{
			claims.Add(new Claim("must_reset_password", "true"));
		}

		SecurityTokenDescriptor descriptor = new()
		{
			Subject = new ClaimsIdentity(claims),
			Expires = DateTime.UtcNow.AddHours(1),
			Issuer = issuer,
			Audience = audience,
			SigningCredentials = credentials,
		};

		JsonWebTokenHandler handler = new();
		return handler.CreateToken(descriptor);
	}

	public string GenerateRefreshToken()
	{
		byte[] bytes = new byte[64];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToBase64String(bytes);
	}

	public TokenIntrospectionResult IntrospectAccessToken(string token)
	{
		string key = configuration[ConfigurationVariables.JwtKey] ?? "build-time-placeholder-key-32-chars!!";
		string issuer = configuration[ConfigurationVariables.JwtIssuer] ?? "receipts-api";
		string audience = configuration[ConfigurationVariables.JwtAudience] ?? "receipts-app";

		SymmetricSecurityKey signingKey = new(Encoding.UTF8.GetBytes(key));

		TokenValidationParameters validationParameters = new()
		{
			ValidateIssuer = true,
			ValidIssuer = issuer,
			ValidateAudience = true,
			ValidAudience = audience,
			ValidateIssuerSigningKey = true,
			IssuerSigningKey = signingKey,
			ValidateLifetime = true,
			ClockSkew = TimeSpan.Zero,
		};

		JsonWebTokenHandler handler = new();
		TokenValidationResult result = handler.ValidateTokenAsync(token, validationParameters).GetAwaiter().GetResult();

		if (!result.IsValid)
		{
			return new TokenIntrospectionResult(Active: false, null, null, null, null, null, null);
		}

		ClaimsIdentity identity = result.ClaimsIdentity;
		string? sub = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		string? email = identity.FindFirst(ClaimTypes.Email)?.Value;
		IEnumerable<string> roles = identity.FindAll(ClaimTypes.Role).Select(c => c.Value);
		string scope = string.Join(" ", roles);

		long? exp = null;
		long? iat = null;

		if (result.SecurityToken is JsonWebToken jwt)
		{
			exp = new DateTimeOffset(jwt.ValidTo).ToUnixTimeSeconds();
			iat = new DateTimeOffset(jwt.IssuedAt).ToUnixTimeSeconds();
		}

		return new TokenIntrospectionResult(
			Active: true,
			Scope: scope,
			Username: email,
			TokenType: "Bearer",
			Exp: exp,
			Iat: iat,
			Sub: sub);
	}
}
