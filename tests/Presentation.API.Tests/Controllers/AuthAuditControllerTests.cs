using System.Security.Claims;
using API.Controllers;
using Application.Interfaces.Services;
using Application.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Moq;
using GeneratedDtos = API.Generated.Dtos;

namespace Presentation.API.Tests.Controllers;

public class AuthAuditControllerTests
{
	private readonly Mock<IAuthAuditService> _authAuditServiceMock;
	private readonly AuthAuditController _controller;

	public AuthAuditControllerTests()
	{
		_authAuditServiceMock = new Mock<IAuthAuditService>();
		_controller = new AuthAuditController(_authAuditServiceMock.Object);
	}

	private void SetupUserClaims(string userId)
	{
		List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId)];
		ClaimsIdentity identity = new(claims, "TestAuth");
		ClaimsPrincipal principal = new(identity);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext { User = principal }
		};
	}

	[Fact]
	public async Task GetMyAuditLog_ReturnsOk_WithUserAuthEvents()
	{
		// Arrange
		string userId = "user-123";
		SetupUserClaims(userId);
		List<AuthAuditEntryDto> logs =
		[
			new AuthAuditEntryDto(Guid.NewGuid(), "Login", userId, null, "testuser@example.com", true, null, "192.168.1.1", "TestAgent/1.0", DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuthAuditEntryDto> pagedResult = new(logs, 1, 0, 50);

		_authAuditServiceMock
			.Setup(s => s.GetMyAuditLogAsync(userId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuthAuditListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuthAuditListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(1);
		okResult.Value.Data.First().UserId.Should().Be(userId);
	}

	[Fact]
	public async Task GetMyAuditLog_ReturnsBadRequest_WhenOffsetIsNegative()
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(-1, 50, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("offset must be non-negative");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public async Task GetMyAuditLog_ReturnsBadRequest_WhenLimitIsOutOfRange(int limit)
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(0, limit, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetRecent_ReturnsOk_WithRecentAuthEvents()
	{
		// Arrange
		List<AuthAuditEntryDto> logs =
		[
			new AuthAuditEntryDto(Guid.NewGuid(), "Login", "user-1", null, "user1@example.com", true, null, "10.0.0.1", "Agent/1.0", DateTimeOffset.UtcNow, null),
			new AuthAuditEntryDto(Guid.NewGuid(), "LoginFailed", "user-2", null, "user2@example.com", false, "Bad password", "10.0.0.2", "Agent/2.0", DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuthAuditEntryDto> pagedResult = new(logs, 2, 0, 50);

		_authAuditServiceMock
			.Setup(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuthAuditListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuthAuditListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(2);
	}

	[Fact]
	public async Task GetRecent_ReturnsBadRequest_WhenOffsetIsNegative()
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(-1, 50, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("offset must be non-negative");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public async Task GetRecent_ReturnsBadRequest_WhenLimitIsOutOfRange(int limit)
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, limit, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetFailed_ReturnsOk_WithFailedAttempts()
	{
		// Arrange
		List<AuthAuditEntryDto> logs =
		[
			new AuthAuditEntryDto(Guid.NewGuid(), "LoginFailed", "user-1", null, "user1@example.com", false, "Invalid password", "10.0.0.1", "Agent/1.0", DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuthAuditEntryDto> pagedResult = new(logs, 1, 0, 50);

		_authAuditServiceMock
			.Setup(s => s.GetFailedAttemptsAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetFailed(0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuthAuditListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuthAuditListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(1);
		okResult.Value.Data.First().Success.Should().BeFalse();
	}

	[Fact]
	public async Task GetFailed_ReturnsBadRequest_WhenOffsetIsNegative()
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetFailed(-1, 50, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("offset must be non-negative");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public async Task GetFailed_ReturnsBadRequest_WhenLimitIsOutOfRange(int limit)
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetFailed(0, limit, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetMyAuditLog_ThrowsException_WhenServiceFails()
	{
		// Arrange
		string userId = "user-123";
		SetupUserClaims(userId);
		_authAuditServiceMock
			.Setup(s => s.GetMyAuditLogAsync(userId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetMyAuditLog(0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetRecent_ThrowsException_WhenServiceFails()
	{
		// Arrange
		_authAuditServiceMock
			.Setup(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetRecent(0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetFailed_ThrowsException_WhenServiceFails()
	{
		// Arrange
		_authAuditServiceMock
			.Setup(s => s.GetFailedAttemptsAsync(0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetFailed(0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetMyAuditLog_InvalidSortBy_ReturnsBadRequest()
	{
		// Arrange
		string userId = "user-123";
		SetupUserClaims(userId);

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(0, 50, "invalid", null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetMyAuditLog_InvalidSortDirection_ReturnsBadRequest()
	{
		// Arrange
		string userId = "user-123";
		SetupUserClaims(userId);

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(0, 50, null, "invalid", CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetMyAuditLog_ReturnsUnauthorized_WhenNoUserId()
	{
		// Arrange - no claims set up (default anonymous context)
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};

		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>, UnauthorizedHttpResult> result = await _controller.GetMyAuditLog(0, 50, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<UnauthorizedHttpResult>(result.Result);
	}

	[Fact]
	public async Task GetRecent_InvalidSortBy_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, "invalid", null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetFailed_InvalidSortDirection_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuthAuditListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetFailed(0, 50, null, "invalid", CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortDirection");
	}
}
