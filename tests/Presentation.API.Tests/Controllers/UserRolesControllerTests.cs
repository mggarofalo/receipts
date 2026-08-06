using API.Controllers;
using API.Generated.Dtos;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class UserRolesControllerTests
{
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
	private readonly UserRolesController _controller;

	public UserRolesControllerTests()
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

		_controller = new UserRolesController(_userManagerMock.Object);
	}

	private static ApplicationUser CreateTestUser(string id = "user-123")
	{
		return new ApplicationUser { Id = id, Email = "test@example.com", UserName = "test@example.com", CreatedAt = DateTimeOffset.UtcNow };
	}

	// ── GetUserRoles ────────────────────────────────────────

	[Fact]
	public async Task GetUserRoles_ReturnsOk_WhenUserExists()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Admin", "User" });

		Results<Ok<UserRolesResponse>, NotFound> result = await _controller.GetUserRoles(user.Id);

		Ok<UserRolesResponse> okResult = Assert.IsType<Ok<UserRolesResponse>>(result.Result);
		okResult.Value!.Roles.Should().BeEquivalentTo(["Admin", "User"]);
	}

	[Fact]
	public async Task GetUserRoles_ReturnsNotFound_WhenUserDoesNotExist()
	{
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<Ok<UserRolesResponse>, NotFound> result = await _controller.GetUserRoles("missing");

		Assert.IsType<NotFound>(result.Result);
	}

	// ── AssignUserRole ──────────────────────────────────────

	[Fact]
	public async Task AssignUserRole_ReturnsNoContent_WhenSuccessful()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.AddToRoleAsync(user, "Admin")).ReturnsAsync(IdentityResult.Success);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.AssignUserRole(user.Id, "Admin");

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task AssignUserRole_ReturnsBadRequest_WhenRoleIsInvalid()
	{
		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.AssignUserRole("user-123", "InvalidRole");

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid role");
	}

	[Fact]
	public async Task AssignUserRole_ReturnsNotFound_WhenUserDoesNotExist()
	{
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.AssignUserRole("missing", "Admin");

		Assert.IsType<NotFound>(result.Result);
	}

	// ── RemoveUserRole ──────────────────────────────────────

	[Fact]
	public async Task RemoveUserRole_ReturnsNoContent_WhenSuccessful()
	{
		ApplicationUser user = CreateTestUser();
		_userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.RemoveFromRoleAsync(user, "Admin")).ReturnsAsync(IdentityResult.Success);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.RemoveUserRole(user.Id, "Admin");

		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task RemoveUserRole_ReturnsBadRequest_WhenRoleIsInvalid()
	{
		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.RemoveUserRole("user-123", "InvalidRole");

		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid role");
	}

	[Fact]
	public async Task RemoveUserRole_ReturnsNotFound_WhenUserDoesNotExist()
	{
		_userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

		Results<NoContent, BadRequest<ProblemDetails>, NotFound> result = await _controller.RemoveUserRole("missing", "Admin");

		Assert.IsType<NotFound>(result.Result);
	}
}
