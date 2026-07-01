using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabSyncEventServiceTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _factory = DbContextHelpers.CreateInMemoryContextFactory();
	private readonly Mock<ICurrentUserAccessor> _currentUserMock = new();

	private YnabSyncEventService CreateService(string? userId = "user-1")
	{
		_currentUserMock.Setup(a => a.UserId).Returns(userId);
		return new YnabSyncEventService(_factory, _currentUserMock.Object, TimeProvider.System);
	}

	private async Task SeedAsync(params YnabSyncEventEntity[] events)
	{
		await using ApplicationDbContext context = _factory.CreateDbContext();
		context.YnabSyncEvents.AddRange(events);
		await context.SaveChangesAsync();
	}

	private static YnabSyncEventEntity Event(YnabSyncEventType type, bool success, DateTimeOffset occurredAt) => new()
	{
		Id = Guid.NewGuid(),
		UserId = "user-1",
		EventType = type,
		Success = success,
		OccurredAt = occurredAt,
	};

	[Fact]
	public async Task WriteAsync_PersistsEvent_WithCurrentUser()
	{
		YnabSyncEventService service = CreateService("user-42");
		Guid receiptId = Guid.NewGuid();

		await service.WriteAsync(YnabSyncEventType.Push, success: true, receiptId: receiptId, httpStatus: 201, cancellationToken: CancellationToken.None);

		await using ApplicationDbContext context = _factory.CreateDbContext();
		YnabSyncEventEntity saved = await context.YnabSyncEvents.SingleAsync();
		saved.UserId.Should().Be("user-42");
		saved.EventType.Should().Be(YnabSyncEventType.Push);
		saved.Success.Should().BeTrue();
		saved.ReceiptId.Should().Be(receiptId);
		saved.HttpStatus.Should().Be(201);
	}

	[Fact]
	public async Task GetRecentAsync_FiltersBySuccess_AndPaginatesMostRecentFirst()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await SeedAsync(
			Event(YnabSyncEventType.Push, true, now.AddMinutes(-1)),
			Event(YnabSyncEventType.Push, false, now.AddMinutes(-2)),
			Event(YnabSyncEventType.Push, false, now.AddMinutes(-3)));

		YnabSyncEventService service = CreateService();

		PagedResult<YnabSyncEventDto> failures = await service.GetRecentAsync(0, 10, SortParams.Default, success: false);
		failures.Total.Should().Be(2);
		failures.Data.Should().OnlyContain(e => !e.Success);

		PagedResult<YnabSyncEventDto> firstPage = await service.GetRecentAsync(0, 1, SortParams.Default);
		firstPage.Total.Should().Be(3);
		firstPage.Data.Should().HaveCount(1);
		firstPage.Data[0].OccurredAt.Should().BeCloseTo(now.AddMinutes(-1), TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task GetStatusAsync_ComputesWindowCountsAndLastTimestamps()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		await SeedAsync(
			Event(YnabSyncEventType.Push, true, now.AddHours(-1)),      // 24h, 7d, 30d
			Event(YnabSyncEventType.Push, false, now.AddDays(-3)),      // 7d, 30d
			Event(YnabSyncEventType.Push, true, now.AddDays(-20)),      // 30d only
			Event(YnabSyncEventType.Push, true, now.AddDays(-40)),      // outside all windows
			Event(YnabSyncEventType.Validate, true, now.AddMinutes(-5)));

		YnabSyncEventService service = CreateService();

		YnabStatus status = await service.GetStatusAsync(isConfigured: true);

		status.IsConfigured.Should().BeTrue();
		status.PushCountLast24h.Should().Be(1);
		status.PushCountLast7d.Should().Be(2);
		status.PushCountLast30d.Should().Be(3);
		status.PushSuccessLast30d.Should().Be(2);
		status.PushFailureLast30d.Should().Be(1);
		status.LastPushSuccessAt.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromSeconds(2));
		status.LastPushFailureAt.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromSeconds(2));
		status.LastValidatedAt.Should().BeCloseTo(now.AddMinutes(-5), TimeSpan.FromSeconds(2));
	}
}
