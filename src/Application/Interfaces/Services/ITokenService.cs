namespace Application.Interfaces.Services;

public record TokenIntrospectionResult(
	bool Active,
	string? Scope,
	string? Username,
	string? TokenType,
	long? Exp,
	long? Iat,
	string? Sub);

public interface ITokenService
{
	string GenerateAccessToken(string userId, string email, IList<string> roles, bool mustResetPassword, string securityStamp, Guid? refreshSessionId = null);
	string GenerateRefreshToken();
	TokenIntrospectionResult IntrospectAccessToken(string token);
}
