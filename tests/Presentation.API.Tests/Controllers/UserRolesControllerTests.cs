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

public class UserRolesControllerTests
{
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
	private readonly Mock<IRoleManagementService> _roleManagementServiceMock = new();
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

		_controller = new UserRolesController(_userManagerMock.Object, _roleManagementServiceMock.Object)
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext
				{
					User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "actor"), new Claim(ClaimTypes.Role, "Admin")], "test")),
				},
			},
		};
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

	[Theory]
	[InlineData(RoleChangeMode.Add, RoleChangeStatus.Success, 204)]
	[InlineData(RoleChangeMode.Add, RoleChangeStatus.NotFound, 404)]
	[InlineData(RoleChangeMode.Add, RoleChangeStatus.Invalid, 400)]
	[InlineData(RoleChangeMode.Add, RoleChangeStatus.Conflict, 409)]
	[InlineData(RoleChangeMode.Remove, RoleChangeStatus.Success, 204)]
	[InlineData(RoleChangeMode.Remove, RoleChangeStatus.NotFound, 404)]
	[InlineData(RoleChangeMode.Remove, RoleChangeStatus.Invalid, 400)]
	[InlineData(RoleChangeMode.Remove, RoleChangeStatus.Conflict, 409)]
	public async Task RoleWrite_ForwardsActorRoleAndCancellation_AndMapsServiceResult(RoleChangeMode mode, RoleChangeStatus status, int expectedStatus)
	{
		using CancellationTokenSource cancellation = new();
		_controller.HttpContext.RequestAborted = cancellation.Token;
		_roleManagementServiceMock.Setup(s => s.ChangeAsync("target", "actor", mode,
			It.Is<IReadOnlyCollection<string>>(roles => roles.SequenceEqual(new[] { "Admin" })), null, cancellation.Token))
			.ReturnsAsync(new RoleChangeResult(status, ["Role policy rejected this change"]));

		var result = mode == RoleChangeMode.Add
			? await _controller.AssignUserRole("target", "Admin")
			: await _controller.RemoveUserRole("target", "Admin");

		((IStatusCodeHttpResult)result.Result).StatusCode.Should().Be(expectedStatus);
		if (status is RoleChangeStatus.Invalid or RoleChangeStatus.Conflict)
		{
			((IValueHttpResult<ProblemDetails>)result.Result).Value!.Detail.Should().Contain("Role policy rejected this change");
		}
		_roleManagementServiceMock.VerifyAll();
		_userManagerMock.Verify(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
		_userManagerMock.Verify(m => m.RemoveFromRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
	}
}
