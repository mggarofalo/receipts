using System.Security.Claims;
using API.Controllers;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class AuthControllerTests
{
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
	private readonly Mock<ITokenService> _tokenServiceMock;
	private readonly Mock<IUserService> _userServiceMock;
	private readonly Mock<IAuthAuditService> _authAuditServiceMock;
	private readonly AuthController _controller;

	public AuthControllerTests()
	{
		Mock<IUserStore<ApplicationUser>> userStoreMock = new();
		_userManagerMock = new Mock<UserManager<ApplicationUser>>(
			userStoreMock.Object,
			new Mock<IOptions<IdentityOptions>>().Object,
			new Mock<IPasswordHasher<ApplicationUser>>().Object,
			Array.Empty<IUserValidator<ApplicationUser>>(),
			Array.Empty<IPasswordValidator<ApplicationUser>>(),
			new Mock<ILookupNormalizer>().Object,
			new Mock<IdentityErrorDescriber>().Object,
			new Mock<IServiceProvider>().Object,
			new Mock<ILogger<UserManager<ApplicationUser>>>().Object);

		_tokenServiceMock = new Mock<ITokenService>();
		_userServiceMock = new Mock<IUserService>();
		// Refresh tokens are persisted hashed. Model the hash deterministically so tests can assert the
		// stored value differs from the plaintext returned to the client.
		_userServiceMock.Setup(s => s.HashRefreshToken(It.IsAny<string>())).Returns<string>(t => "hashed:" + t);
		_authAuditServiceMock = new Mock<IAuthAuditService>();
		Mock<ILogger<AuthController>> loggerMock = ControllerTestHelpers.GetLoggerMock<AuthController>();

		_controller = new AuthController(
			_userManagerMock.Object,
			_tokenServiceMock.Object,
			_userServiceMock.Object,
			_authAuditServiceMock.Object,
			loggerMock.Object);

		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	private void SetupUserClaims(string userId)
	{
		List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId)];
		ClaimsIdentity identity = new(claims, "TestAuth");
		ClaimsPrincipal principal = new(identity);
		_controller.ControllerContext.HttpContext.User = principal;
	}

	private ApplicationUser CreateTestUser(string id = "user-123", string email = "test@example.com")
	{
		return new ApplicationUser
		{
			Id = id,
			Email = email,
			UserName = email,
			RefreshToken = "valid-refresh-token",
			RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
			MustResetPassword = false,
			CreatedAt = DateTimeOffset.UtcNow,
		};
	}

	// ── Login ────────────────────────────────────────────────

	[Fact]
	public async Task Login_ReturnsTokenResponse_WithTokenTypeAndScope()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByEmailAsync("test@example.com")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.CheckPasswordAsync(user, "password")).ReturnsAsync(true);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Admin", "User" });
		_tokenServiceMock.Setup(t => t.GenerateAccessToken(user.Id, user.Email!, It.IsAny<IList<string>>(), false)).Returns("access-token");
		_tokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns("refresh-token");

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>> result = await _controller.Login(new LoginRequest { Email = "test@example.com", Password = "password" });

		// Assert
		Ok<TokenResponse> okResult = Assert.IsType<Ok<TokenResponse>>(result.Result);
		TokenResponse response = okResult.Value!;
		response.TokenType.Should().Be("Bearer");
		response.Scope.Should().Be("Admin User");
		response.AccessToken.Should().Be("access-token");
		response.RefreshToken.Should().Be("refresh-token");
		response.ExpiresIn.Should().Be(3600);
	}

	[Fact]
	public async Task Login_InvalidCredentials_ReturnsOAuthErrorResponse()
	{
		// Arrange
		_userManagerMock.Setup(m => m.FindByEmailAsync("test@example.com")).ReturnsAsync((ApplicationUser?)null);

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>> result = await _controller.Login(new LoginRequest { Email = "test@example.com", Password = "wrong" });

		// Assert
		JsonHttpResult<OAuthErrorResponse> errorResult = Assert.IsType<JsonHttpResult<OAuthErrorResponse>>(result.Result);
		errorResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
		errorResult.Value!.Error.Should().Be(OAuthErrorResponseError.Invalid_grant);
		errorResult.Value!.Error_description.Should().Be("Invalid email or password");
	}

	[Fact]
	public async Task Login_LockedOut_ReturnsOAuthErrorResponse()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByEmailAsync("test@example.com")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.CheckPasswordAsync(user, "password")).ReturnsAsync(true);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>> result = await _controller.Login(new LoginRequest { Email = "test@example.com", Password = "password" });

		// Assert
		JsonHttpResult<OAuthErrorResponse> errorResult = Assert.IsType<JsonHttpResult<OAuthErrorResponse>>(result.Result);
		errorResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
		errorResult.Value!.Error.Should().Be(OAuthErrorResponseError.Invalid_grant);
		errorResult.Value!.Error_description.Should().Be("Account is disabled");
	}

	[Fact]
	public async Task Login_StoresHashedRefreshToken_NotPlaintext()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByEmailAsync("test@example.com")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.CheckPasswordAsync(user, "password")).ReturnsAsync(true);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "User" });
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
		_tokenServiceMock.Setup(t => t.GenerateAccessToken(user.Id, user.Email!, It.IsAny<IList<string>>(), false)).Returns("access-token");
		_tokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns("plaintext-refresh");

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>> result = await _controller.Login(new LoginRequest { Email = "test@example.com", Password = "password" });

		// Assert — the client receives the plaintext token, but only the hash is persisted.
		Ok<TokenResponse> okResult = Assert.IsType<Ok<TokenResponse>>(result.Result);
		okResult.Value!.RefreshToken.Should().Be("plaintext-refresh");
		user.RefreshToken.Should().Be("hashed:plaintext-refresh");
		user.RefreshToken.Should().NotBe("plaintext-refresh");
	}

	// ── Refresh ──────────────────────────────────────────────

	[Fact]
	public async Task RefreshToken_ReturnsTokenResponse_WithTokenTypeAndScope()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("valid-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "User" });
		_tokenServiceMock.Setup(t => t.GenerateAccessToken(user.Id, user.Email!, It.IsAny<IList<string>>(), false)).Returns("new-access-token");
		_tokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns("new-refresh-token");

		// Act
		Results<Ok<TokenResponse>, UnauthorizedHttpResult> result = await _controller.RefreshToken(new RefreshTokenRequest { RefreshToken = "valid-refresh-token" }, CancellationToken.None);

		// Assert
		Ok<TokenResponse> okResult = Assert.IsType<Ok<TokenResponse>>(result.Result);
		TokenResponse response = okResult.Value!;
		response.TokenType.Should().Be("Bearer");
		response.Scope.Should().Be("User");
	}

	// ── ChangePassword ───────────────────────────────────────

	[Fact]
	public async Task ChangePassword_ReturnsTokenResponse_WithTokenTypeAndScope()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		SetupUserClaims(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.ChangePasswordAsync(user, "old", "new")).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Admin" });
		_tokenServiceMock.Setup(t => t.GenerateAccessToken(user.Id, user.Email!, It.IsAny<IList<string>>(), false)).Returns("access-token");
		_tokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns("refresh-token");

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>, UnauthorizedHttpResult> result = await _controller.ChangePassword(new ChangePasswordRequest { CurrentPassword = "old", NewPassword = "new" });

		// Assert
		Ok<TokenResponse> okResult = Assert.IsType<Ok<TokenResponse>>(result.Result);
		TokenResponse response = okResult.Value!;
		response.TokenType.Should().Be("Bearer");
		response.Scope.Should().Be("Admin");
	}

	[Fact]
	public async Task ChangePassword_Failed_ReturnsOAuthErrorResponse()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		SetupUserClaims(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.ChangePasswordAsync(user, "old", "bad"))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "PasswordTooShort", Description = "Password too short" }));

		// Act
		Results<Ok<TokenResponse>, JsonHttpResult<OAuthErrorResponse>, UnauthorizedHttpResult> result = await _controller.ChangePassword(new ChangePasswordRequest { CurrentPassword = "old", NewPassword = "bad" });

		// Assert
		JsonHttpResult<OAuthErrorResponse> errorResult = Assert.IsType<JsonHttpResult<OAuthErrorResponse>>(result.Result);
		errorResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
		errorResult.Value!.Error.Should().Be(OAuthErrorResponseError.Invalid_request);
		errorResult.Value!.Error_description.Should().Contain("Password too short");
	}

	// ── Introspect ───────────────────────────────────────────

	[Fact]
	public async Task IntrospectToken_ValidAccessToken_ReturnsActive()
	{
		// Arrange
		SetupUserClaims("user-123");
		_tokenServiceMock.Setup(t => t.IntrospectAccessToken("valid-access-token"))
			.Returns(new TokenIntrospectionResult(true, "Admin", "test@example.com", "Bearer", 1700000000, 1699990000, "user-123"));

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "valid-access-token", TokenTypeHint = TokenIntrospectionRequestTokenTypeHint.AccessToken },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeTrue();
		response.Scope.Should().Be("Admin");
		response.Username.Should().Be("test@example.com");
		response.TokenType.Should().Be("Bearer");
		response.Sub.Should().Be("user-123");
	}

	[Fact]
	public async Task IntrospectToken_ExpiredAccessToken_ReturnsInactive()
	{
		// Arrange
		SetupUserClaims("user-123");
		_tokenServiceMock.Setup(t => t.IntrospectAccessToken("expired-token"))
			.Returns(new TokenIntrospectionResult(false, null, null, null, null, null, null));

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "expired-token", TokenTypeHint = TokenIntrospectionRequestTokenTypeHint.AccessToken },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeFalse();
	}

	[Fact]
	public async Task IntrospectToken_InvalidAccessToken_ReturnsInactive()
	{
		// Arrange
		SetupUserClaims("user-123");
		_tokenServiceMock.Setup(t => t.IntrospectAccessToken("garbage"))
			.Returns(new TokenIntrospectionResult(false, null, null, null, null, null, null));

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "garbage" },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeFalse();
	}

	[Fact]
	public async Task IntrospectToken_ValidRefreshToken_ReturnsActive()
	{
		// Arrange
		SetupUserClaims("user-123");
		ApplicationUser user = CreateTestUser();
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("valid-refresh", It.IsAny<CancellationToken>())).ReturnsAsync(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "User" });

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "valid-refresh", TokenTypeHint = TokenIntrospectionRequestTokenTypeHint.RefreshToken },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeTrue();
		response.TokenType.Should().Be("refresh_token");
		response.Sub.Should().Be(user.Id);
		response.Scope.Should().Be("User");
	}

	[Fact]
	public async Task IntrospectToken_ExpiredRefreshToken_ReturnsInactive()
	{
		// Arrange
		SetupUserClaims("user-123");
		ApplicationUser user = CreateTestUser();
		user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("expired-refresh", It.IsAny<CancellationToken>())).ReturnsAsync(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "expired-refresh", TokenTypeHint = TokenIntrospectionRequestTokenTypeHint.RefreshToken },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeFalse();
	}

	[Fact]
	public async Task IntrospectToken_UnknownRefreshToken_ReturnsInactive()
	{
		// Arrange
		SetupUserClaims("user-123");
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("unknown", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

		// Act
		Ok<TokenIntrospectionResponse> result = await _controller.IntrospectToken(
			new TokenIntrospectionRequest { Token = "unknown", TokenTypeHint = TokenIntrospectionRequestTokenTypeHint.RefreshToken },
			CancellationToken.None);

		// Assert
		TokenIntrospectionResponse response = result.Value!;
		response.Active.Should().BeFalse();
	}

	// ── Revoke ───────────────────────────────────────────────

	[Fact]
	public async Task RevokeToken_ValidRefreshToken_ReturnsOk()
	{
		// Arrange
		SetupUserClaims("user-123");
		ApplicationUser user = CreateTestUser();
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("valid-refresh", It.IsAny<CancellationToken>())).ReturnsAsync(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

		// Act
		Ok result = await _controller.RevokeToken(
			new TokenRevocationRequest { Token = "valid-refresh" },
			CancellationToken.None);

		// Assert
		Assert.IsType<Ok>(result);
		user.RefreshToken.Should().BeNull();
		user.RefreshTokenExpiresAt.Should().BeNull();
	}

	[Fact]
	public async Task RevokeToken_InvalidToken_ReturnsOk()
	{
		// Arrange
		SetupUserClaims("user-123");
		_userServiceMock.Setup(s => s.FindUserIdByRefreshTokenAsync("invalid", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

		// Act
		Ok result = await _controller.RevokeToken(
			new TokenRevocationRequest { Token = "invalid" },
			CancellationToken.None);

		// Assert
		Assert.IsType<Ok>(result);
	}

	// ── Logout ──────────────────────────────────────────────

	[Fact]
	public async Task Logout_ReturnsNoContent_AndClearsRefreshToken()
	{
		// Arrange
		ApplicationUser user = CreateTestUser();
		SetupUserClaims(user.Id);
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

		// Act
		Results<NoContent, UnauthorizedHttpResult> result = await _controller.Logout();

		// Assert
		Assert.IsType<NoContent>(result.Result);
		user.RefreshToken.Should().BeNull();
		user.RefreshTokenExpiresAt.Should().BeNull();
		_userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
	}

	[Fact]
	public async Task Logout_ReturnsUnauthorized_WhenNoClaims()
	{
		// Act
		Results<NoContent, UnauthorizedHttpResult> result = await _controller.Logout();

		// Assert
		Assert.IsType<UnauthorizedHttpResult>(result.Result);
	}
}
