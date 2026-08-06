using API.Controllers;
using Application.Interfaces.Services;
using Application.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using AppAuditLogDto = Application.Interfaces.Services.AuditLogDto;
using GeneratedDtos = API.Generated.Dtos;

namespace Presentation.API.Tests.Controllers;

public class AuditControllerTests
{
	private readonly Mock<IAuditService> _auditServiceMock;
	private readonly AuditController _controller;

	public AuditControllerTests()
	{
		_auditServiceMock = new Mock<IAuditService>();
		_controller = new AuditController(_auditServiceMock.Object);
	}

	[Fact]
	public async Task GetAuditLogs_WithEntityParams_ReturnsOk_WithAuditLogs()
	{
		// Arrange
		string entityType = "Account";
		string entityId = Guid.NewGuid().ToString();
		List<AppAuditLogDto> logs =
		[
			new AppAuditLogDto(Guid.NewGuid(), entityType, entityId, "Create", "[]", null, null, DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuditLogDto> pagedResult = new(logs, 1, 0, 50);

		_auditServiceMock.Setup(s => s.GetByEntityAsync(entityType, entityId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(entityType, entityId, null, null, 0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuditLogListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuditLogListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(1);
		okResult.Value.Total.Should().Be(1);
	}

	[Fact]
	public async Task GetAuditLogs_WithEntityParams_NoLogs_ReturnsOk_EmptyList()
	{
		// Arrange
		string entityType = "Account";
		string entityId = Guid.NewGuid().ToString();
		PagedResult<AuditLogDto> pagedResult = new([], 0, 0, 50);

		_auditServiceMock.Setup(s => s.GetByEntityAsync(entityType, entityId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(entityType, entityId, null, null, 0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuditLogListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuditLogListResponse>>(result.Result);
		okResult.Value!.Data.Should().BeEmpty();
		okResult.Value.Total.Should().Be(0);
	}

	[Fact]
	public async Task GetRecent_ReturnsOk_WithPagedAuditLogs()
	{
		// Arrange
		List<AppAuditLogDto> logs =
		[
			new AppAuditLogDto(Guid.NewGuid(), "Account", Guid.NewGuid().ToString(), "Create", "[]", null, null, DateTimeOffset.UtcNow, null),
			new AppAuditLogDto(Guid.NewGuid(), "Receipt", Guid.NewGuid().ToString(), "Update", "[]", null, null, DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuditLogDto> pagedResult = new(logs, 2, 0, 50);

		_auditServiceMock.Setup(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, null, null, null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuditLogListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuditLogListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(2);
		okResult.Value.Total.Should().Be(2);
	}

	[Fact]
	public async Task GetRecent_WithFilters_PassesFiltersToService()
	{
		// Arrange
		List<AppAuditLogDto> logs =
		[
			new AppAuditLogDto(Guid.NewGuid(), "Account", Guid.NewGuid().ToString(), "Create", "[]", null, null, DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuditLogDto> pagedResult = new(logs, 1, 0, 50);

		_auditServiceMock.Setup(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), "Account", "Created", "abc", It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		await _controller.GetRecent(0, 50, null, null, "Account", "Created", "abc", null, null, CancellationToken.None);

		// Assert
		_auditServiceMock.Verify(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), "Account", "Created", "abc", null, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetRecent_ReturnsBadRequest_WhenOffsetIsNegative()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(-1, 50, null, null, null, null, null, null, null, CancellationToken.None);

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
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, limit, null, null, null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetRecent_WithInvalidSortBy_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, "invalidColumn", null, null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetRecent_WithInvalidSortDirection_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, null, "invalid", null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetAuditLogs_ReturnsBadRequest_WhenOffsetIsNegative()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(
			"Account", Guid.NewGuid().ToString(), null, null, -1, 50, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("offset must be non-negative");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(501)]
	public async Task GetAuditLogs_ReturnsBadRequest_WhenLimitIsOutOfRange(int limit)
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(
			"Account", Guid.NewGuid().ToString(), null, null, 0, limit, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAuditLogs_WithUserId_ReturnsOk_WithUserLogs()
	{
		// Arrange
		string userId = "test-user";
		List<AppAuditLogDto> logs =
		[
			new AppAuditLogDto(Guid.NewGuid(), "Account", Guid.NewGuid().ToString(), "Create", "[]", userId, null, DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuditLogDto> pagedResult = new(logs, 1, 0, 50);

		_auditServiceMock.Setup(s => s.GetByUserAsync(userId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(null, null, userId, null, 0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuditLogListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuditLogListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(1);
		okResult.Value.Data.First().ChangedByUserId.Should().Be(userId);
	}

	[Fact]
	public async Task GetAuditLogs_WithApiKeyId_ReturnsOk_WithApiKeyLogs()
	{
		// Arrange
		Guid apiKeyId = Guid.NewGuid();
		List<AppAuditLogDto> logs =
		[
			new AppAuditLogDto(Guid.NewGuid(), "Account", Guid.NewGuid().ToString(), "Create", "[]", null, apiKeyId, DateTimeOffset.UtcNow, null)
		];
		PagedResult<AuditLogDto> pagedResult = new(logs, 1, 0, 50);

		_auditServiceMock.Setup(s => s.GetByApiKeyAsync(apiKeyId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(pagedResult);

		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(null, null, null, apiKeyId, 0, 50, null, null, CancellationToken.None);

		// Assert
		Ok<GeneratedDtos.AuditLogListResponse> okResult = Assert.IsType<Ok<GeneratedDtos.AuditLogListResponse>>(result.Result);
		okResult.Value!.Data.Should().HaveCount(1);
		okResult.Value.Data.First().ChangedByApiKeyId.Should().Be(apiKeyId);
	}

	[Fact]
	public async Task GetAuditLogs_WithEntityParams_ThrowsException_WhenServiceFails()
	{
		// Arrange
		string entityType = "Account";
		string entityId = Guid.NewGuid().ToString();

		_auditServiceMock.Setup(s => s.GetByEntityAsync(entityType, entityId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetAuditLogs(entityType, entityId, null, null, 0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetRecent_ThrowsException_WhenServiceFails()
	{
		// Arrange
		_auditServiceMock.Setup(s => s.GetRecentAsync(0, 50, It.IsAny<SortParams>(), null, null, null, null, null, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetRecent(0, 50, null, null, null, null, null, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAuditLogs_WithUserId_ThrowsException_WhenServiceFails()
	{
		// Arrange
		string userId = "test-user";
		_auditServiceMock.Setup(s => s.GetByUserAsync(userId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetAuditLogs(null, null, userId, null, 0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAuditLogs_WithApiKeyId_ThrowsException_WhenServiceFails()
	{
		// Arrange
		Guid apiKeyId = Guid.NewGuid();
		_auditServiceMock.Setup(s => s.GetByApiKeyAsync(apiKeyId, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception("Test error"));

		// Act
		Func<Task> act = () => _controller.GetAuditLogs(null, null, null, apiKeyId, 0, 50, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAuditLogs_InvalidSortBy_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(
			"Account", Guid.NewGuid().ToString(), null, null, 0, 50, "invalid", null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetAuditLogs_InvalidSortDirection_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAuditLogs(
			"Account", Guid.NewGuid().ToString(), null, null, 0, 50, null, "invalid", CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetRecent_InvalidSortBy_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, "invalid", null, null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetRecent_InvalidSortDirection_ReturnsBadRequest()
	{
		// Act
		Results<Ok<GeneratedDtos.AuditLogListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetRecent(0, 50, null, "invalid", null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badRequest = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequest.Value!.Detail.Should().Contain("Invalid sortDirection");
	}
}
