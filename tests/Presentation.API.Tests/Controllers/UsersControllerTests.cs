using System.Security.Claims;
using API.Controllers;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
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

public class UsersControllerTests
{
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
	private readonly Mock<IUserService> _userServiceMock;
	private readonly Mock<IAuthAuditService> _authAuditServiceMock;
	private readonly Mock<IApiKeyService> _apiKeyServiceMock;
	private readonly Mock<ILogger<UsersController>> _loggerMock;
	private readonly Mock<IRoleManagementService> _roleManagementServiceMock = new();
	private readonly UsersController _controller;

	public UsersControllerTests()
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

		_userServiceMock = new Mock<IUserService>();
		_authAuditServiceMock = new Mock<IAuthAuditService>();
		_apiKeyServiceMock = new Mock<IApiKeyService>();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<UsersController>();

		_controller = new UsersController(
			_userServiceMock.Object,
			_userManagerMock.Object,
			_authAuditServiceMock.Object,
			_apiKeyServiceMock.Object,
			_loggerMock.Object,
			_roleManagementServiceMock.Object);

		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
		SetupUserClaims("admin-1");
	}

	private void SetupUserClaims(string userId)
	{
		List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, "Admin")];
		ClaimsIdentity identity = new(claims, "TestAuth");
		ClaimsPrincipal principal = new(identity);
		_controller.ControllerContext.HttpContext.User = principal;
	}

	private static ApplicationUser CreateTestUser(string id = "user-123", string email = "test@example.com")
	{
		return new ApplicationUser
		{
			Id = id,
			Email = email,
			UserName = email,
			FirstName = "Test",
			LastName = "User",
			CreatedAt = DateTimeOffset.UtcNow,
		};
	}

	// ── ListUsers ───────────────────────────────────────────

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task ListUsers_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		Results<Ok<UserListResponse>, BadRequest<ProblemDetails>> result = await _controller.ListUsers(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task ListUsers_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		Results<Ok<UserListResponse>, BadRequest<ProblemDetails>> result = await _controller.ListUsers(offset, limit, null, null);

		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task ListUsers_ReturnsOk_WithUserList()
	{
		List<UserSummary> users =
		[
			new("u1", "a@b.com", "A", "B", ["Admin"], false, DateTimeOffset.UtcNow, null),
			new("u2", "c@d.com", "C", "D", ["User"], false, DateTimeOffset.UtcNow, null),
		];
		_userServiceMock.Setup(s => s.ListUsersAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<UserSummary>(users, 2, 0, 50));

		Results<Ok<UserListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.ListUsers(0, 50, null, null);

		Ok<UserListResponse> result = Assert.IsType<Ok<UserListResponse>>(rawResult.Result);
		UserListResponse response = result.Value!;
		response.Data.Should().HaveCount(2);
		response.Total.Should().Be(2);
	}

	// ── GetUser ─────────────────────────────────────────────

	[Fact]
	public async Task GetUser_ReturnsOk_WhenUserExists()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Admin" });

		Results<Ok<UserSummaryResponse>, NotFound> result = await _controller.GetUser(user.Id);

		Ok<UserSummaryResponse> okResult = Assert.IsType<Ok<UserSummaryResponse>>(result.Result);
		okResult.Value!.Email.Should().Be(user.Email);
	}

	[Fact]
	public async Task GetUser_ReturnsNotFound_WhenUserDoesNotExist()
	{
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<Ok<UserSummaryResponse>, NotFound> result = await _controller.GetUser("missing");

		Assert.IsType<NotFound>(result.Result);
	}

	// ── CreateUser ──────────────────────────────────────────

	[Fact]
	public async Task CreateUser_ReturnsOk_WhenSuccessful()
	{
		_userManagerMock.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Password1!"))
			.ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), "User"))
			.ReturnsAsync(IdentityResult.Success);

		Results<Ok<UserSummaryResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateUser(
			new CreateUserRequest { Email = "new@example.com", Password = "Password1!", FirstName = "New", LastName = "User", Role = "User" });

		Ok<UserSummaryResponse> okResult = Assert.IsType<Ok<UserSummaryResponse>>(result.Result);
		okResult.Value!.Email.Should().Be("new@example.com");
	}

	[Fact]
	public async Task CreateUser_ReturnsBadRequest_WhenCreateFails()
	{
		_userManagerMock.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Password too weak" }));

		Results<Ok<UserSummaryResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateUser(
			new CreateUserRequest { Email = "new@example.com", Password = "weak", FirstName = "New", LastName = "User", Role = "User" });

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Password too weak");
	}

	[Fact]
	public async Task CreateUser_ReturnsBadRequest_WhenRoleAssignmentFails()
	{
		_userManagerMock.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Role not found" }));

		Results<Ok<UserSummaryResponse>, BadRequest<ProblemDetails>> result = await _controller.CreateUser(
			new CreateUserRequest { Email = "new@example.com", Password = "Password1!", FirstName = "New", LastName = "User", Role = "BadRole" });

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Role not found");
	}

	// Role/profile policy and Identity atomicity are exercised with PostgreSQL in
	// RoleManagementTests. These tests check the controller boundary and key side effect.
	[Fact]
	public async Task UpdateUser_ForwardsActorProfileAndCancellation_AsOneRoleChange()
	{
		SetupUserClaims("admin-1");
		using CancellationTokenSource cancellation = new();
		_controller.HttpContext.RequestAborted = cancellation.Token;
		UserProfileUpdate profile = new("updated@example.com", "Up", "Dated", false);
		_roleManagementServiceMock.Setup(s => s.ChangeAsync("user-123", "admin-1", RoleChangeMode.Replace,
			It.Is<IReadOnlyCollection<string>>(roles => roles.SequenceEqual(new[] { "Admin" })), profile, cancellation.Token))
			.ReturnsAsync(RoleChangeResult.Success);

		var result = await _controller.UpdateUser("user-123", new UpdateUserRequest
		{
			Email = profile.Email,
			FirstName = profile.FirstName,
			LastName = profile.LastName,
			Role = "Admin",
			IsDisabled = false,
		});

		Assert.IsType<NoContent>(result.Result);
		_roleManagementServiceMock.VerifyAll();
		_userManagerMock.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
		_apiKeyServiceMock.Verify(s => s.RevokeAllForUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(RoleChangeStatus.Success, 204)]
	[InlineData(RoleChangeStatus.NotFound, 404)]
	[InlineData(RoleChangeStatus.Invalid, 400)]
	[InlineData(RoleChangeStatus.Conflict, 409)]
	public async Task UpdateUser_MapsResult_AndRevokesKeysOnlyAfterSuccessfulDisable(RoleChangeStatus status, int expectedStatus)
	{
		_roleManagementServiceMock.Setup(s => s.ChangeAsync(It.IsAny<string>(), It.IsAny<string>(), RoleChangeMode.Replace,
			It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<UserProfileUpdate>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RoleChangeResult(status, ["Role update rejected"]));

		var result = await _controller.UpdateUser("user-123", new UpdateUserRequest
		{
			Email = "updated@example.com",
			Role = "User",
			IsDisabled = true,
		});

		((IStatusCodeHttpResult)result.Result).StatusCode.Should().Be(expectedStatus);
		if (status is RoleChangeStatus.Invalid or RoleChangeStatus.Conflict)
		{
			((IValueHttpResult<ProblemDetails>)result.Result).Value!.Detail.Should().Contain("Role update rejected");
		}
		_apiKeyServiceMock.Verify(s => s.RevokeAllForUserAsync("user-123", It.IsAny<CancellationToken>()),
			status == RoleChangeStatus.Success ? Times.Once() : Times.Never());
	}

	// ── DeactivateUser ──────────────────────────────────────

	[Fact]
	public async Task DeactivateUser_ReturnsNoContent_WhenSuccessful()
	{
		SetupUserClaims("admin-1");
		ApplicationUser user = CreateTestUser("user-123");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.DeactivateUser("user-123");

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeactivateUser_RevokesAllApiKeys_WhenSuccessful()
	{
		SetupUserClaims("admin-1");
		ApplicationUser user = CreateTestUser("user-123");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

		await _controller.DeactivateUser("user-123");

		_apiKeyServiceMock.Verify(s => s.RevokeAllForUserAsync("user-123", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DeactivateUser_RotatesSecurityStamp_WhenSuccessful()
	{
		SetupUserClaims("admin-1");
		ApplicationUser user = CreateTestUser("user-123");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);

		await _controller.DeactivateUser("user-123");

		// Clearing the refresh token only stops renewal; rotating the stamp kills the live access token too.
		_userManagerMock.Verify(m => m.UpdateSecurityStampAsync(user), Times.Once);
	}

	[Fact]
	public async Task DeactivateUser_ReturnsBadRequest_WhenSelfDeactivate()
	{
		SetupUserClaims("user-123");

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.DeactivateUser("user-123");

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Cannot deactivate your own account");
	}

	[Fact]
	public async Task DeactivateUser_ReturnsNotFound_WhenUserDoesNotExist()
	{
		SetupUserClaims("admin-1");
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.DeactivateUser("missing");

		Assert.IsType<NotFound>(result.Result);
	}

	// ── AdminResetPassword ──────────────────────────────────

	[Fact]
	public async Task AdminResetPassword_ReturnsNoContent_WhenSuccessful()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddPasswordAsync(user, "NewPassword1!")).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.AdminResetPassword(
			user.Id,
			new AdminResetPasswordRequest { NewPassword = "NewPassword1!" });

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task AdminResetPassword_RevokesAllApiKeys_WhenSuccessful()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddPasswordAsync(user, "NewPassword1!")).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

		await _controller.AdminResetPassword(user.Id, new AdminResetPasswordRequest { NewPassword = "NewPassword1!" });

		_apiKeyServiceMock.Verify(s => s.RevokeAllForUserAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task AdminResetPassword_RotatesSecurityStamp_WhenSuccessful()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddPasswordAsync(user, "NewPassword1!")).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);

		await _controller.AdminResetPassword(user.Id, new AdminResetPasswordRequest { NewPassword = "NewPassword1!" });

		// Access tokens minted under the old password must die immediately (RECEIPTS-800).
		_userManagerMock.Verify(m => m.UpdateSecurityStampAsync(user), Times.Once);
	}

	[Fact]
	public async Task AdminResetPassword_ReturnsNotFound_WhenUserDoesNotExist()
	{
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.AdminResetPassword(
			"missing",
			new AdminResetPasswordRequest { NewPassword = "NewPassword1!" });

		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task AdminResetPassword_ReturnsBadRequest_WhenPasswordFails()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
		_userManagerMock.Setup(m => m.AddPasswordAsync(user, "weak"))
			.ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Password too weak" }));

		Results<NoContent, NotFound, BadRequest<ProblemDetails>> result = await _controller.AdminResetPassword(
			user.Id,
			new AdminResetPasswordRequest { NewPassword = "weak" });

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Password too weak");
	}
}
