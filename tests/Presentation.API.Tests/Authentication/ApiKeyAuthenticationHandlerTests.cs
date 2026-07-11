using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Authentication;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Authentication;

public class ApiKeyAuthenticationHandlerTests
{
	private readonly Mock<IApiKeyService> _apiKeyServiceMock;
	private readonly Mock<IAuthAuditService> _authAuditServiceMock;
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
	private readonly ApiKeyAuthenticationOptions _options;

	public ApiKeyAuthenticationHandlerTests()
	{
		_apiKeyServiceMock = new Mock<IApiKeyService>();
		_authAuditServiceMock = new Mock<IAuthAuditService>();
		_userManagerMock = new Mock<UserManager<ApplicationUser>>(
			Mock.Of<IUserStore<ApplicationUser>>(),
			null!, null!, null!, null!, null!, null!, null!, null!);
		_options = new ApiKeyAuthenticationOptions();
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsNoResult_WhenNoHeader()
	{
		// Arrange
		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(new DefaultHttpContext());

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.None.Should().BeTrue();
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsNoResult_WhenEmptyHeader()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "";

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.None.Should().BeTrue();
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsFail_WhenInvalidKey()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "invalid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("invalid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync((ApiKeyValidationResult?)null);

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeFalse();
		result.Failure!.Message.Should().Be("Invalid API key.");
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsSuccess_WithClaimsForValidKey()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "test@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(false);
		_userManagerMock.Setup(u => u.GetRolesAsync(user))
			.ReturnsAsync(new List<string> { "Admin" });

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeTrue();
		ClaimsPrincipal principal = result.Principal!;
		principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(userId);
		principal.FindFirst(ClaimTypes.Email)!.Value.Should().Be("test@example.com");
		principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be("Admin");
		principal.FindFirst("ApiKeyId")!.Value.Should().Be(keyId.ToString());
		principal.FindFirst("BypassRateLimit")!.Value.Should().Be("false");
	}

	[Fact]
	public async Task HandleAuthenticateAsync_SetsBypassRateLimitClaim_WhenKeyHasBypass()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "bypass@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, true));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(false);
		_userManagerMock.Setup(u => u.GetRolesAsync(user))
			.ReturnsAsync(new List<string>());

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeTrue();
		result.Principal!.FindFirst("BypassRateLimit")!.Value.Should().Be("true");
	}

	[Fact]
	public async Task HandleAuthenticateAsync_OmitsEmailClaim_WhenUserHasNullEmail()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = null };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(false);
		_userManagerMock.Setup(u => u.GetRolesAsync(user))
			.ReturnsAsync(new List<string>());

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeTrue();
		result.Principal!.FindFirst(ClaimTypes.Email).Should().BeNull();
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsFail_WhenUserNotFound()
	{
		// Arrange — a key whose owning account has been deleted must no longer authenticate.
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync((ApplicationUser?)null);

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeFalse();
		result.Principal.Should().BeNull();
	}

	[Fact]
	public async Task HandleAuthenticateAsync_ReturnsFail_WhenUserLockedOut()
	{
		// Arrange — deactivation is implemented as Identity lockout, so a locked-out
		// (deactivated) owner's key must be rejected (RECEIPTS-757).
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "locked@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(true);

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeFalse();
		result.Principal.Should().BeNull();
		// Roles must never be granted for a rejected key.
		_userManagerMock.Verify(u => u.GetRolesAsync(It.IsAny<ApplicationUser>()), Times.Never);
	}

	[Fact]
	public async Task HandleAuthenticateAsync_LogsFailureAuditEvent_WhenUserLockedOut()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "locked@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(true);

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		await handler.AuthenticateAsync();

		// Assert
		_authAuditServiceMock.Verify(a => a.LogAsync(
			It.Is<AuthAuditEntryDto>(e =>
				e.UserId == userId
				&& e.ApiKeyId == keyId
				&& e.EventType == "ApiKeyUsed"
				&& !e.Success
				&& e.FailureReason != null),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task HandleAuthenticateAsync_CallsAuditLog_WithKeyId_WhenValidKey()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "test@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(false);
		_userManagerMock.Setup(u => u.GetRolesAsync(user))
			.ReturnsAsync(new List<string>());

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		await handler.AuthenticateAsync();

		// Assert
		_authAuditServiceMock.Verify(a => a.LogAsync(
			It.Is<AuthAuditEntryDto>(e =>
				e.UserId == userId
				&& e.ApiKeyId == keyId
				&& e.EventType == "ApiKeyUsed"
				&& e.Success),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task HandleAuthenticateAsync_StillReturnsSuccess_WhenAuditLogThrows()
	{
		// Arrange
		string userId = Guid.NewGuid().ToString();
		Guid keyId = Guid.NewGuid();
		ApplicationUser user = new() { Id = userId, Email = "test@example.com" };

		DefaultHttpContext context = new();
		context.Request.Headers["X-API-Key"] = "valid-key";

		_apiKeyServiceMock.Setup(s => s.GetUserIdByApiKeyAsync("valid-key", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ApiKeyValidationResult(userId, keyId, false));
		_userManagerMock.Setup(u => u.FindByIdAsync(userId))
			.ReturnsAsync(user);
		_userManagerMock.Setup(u => u.IsLockedOutAsync(user))
			.ReturnsAsync(false);
		_userManagerMock.Setup(u => u.GetRolesAsync(user))
			.ReturnsAsync(new List<string>());
		_authAuditServiceMock.Setup(a => a.LogAsync(It.IsAny<AuthAuditEntryDto>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("DB connection failed"));

		ApiKeyAuthenticationHandler handler = await CreateHandlerAsync(context);

		// Act
		AuthenticateResult result = await handler.AuthenticateAsync();

		// Assert
		result.Succeeded.Should().BeTrue();
	}

	private async Task<ApiKeyAuthenticationHandler> CreateHandlerAsync(HttpContext context)
	{
		Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>> optionsMonitorMock = new();
		optionsMonitorMock.Setup(o => o.Get(ApiKeyAuthenticationDefaults.AuthenticationScheme))
			.Returns(_options);

		ApiKeyAuthenticationHandler handler = new(
			optionsMonitorMock.Object,
			NullLoggerFactory.Instance,
			UrlEncoder.Default,
			_apiKeyServiceMock.Object,
			_authAuditServiceMock.Object,
			_userManagerMock.Object);

		AuthenticationScheme scheme = new(
			ApiKeyAuthenticationDefaults.AuthenticationScheme,
			null,
			typeof(ApiKeyAuthenticationHandler));

		await handler.InitializeAsync(scheme, context);
		return handler;
	}
}
