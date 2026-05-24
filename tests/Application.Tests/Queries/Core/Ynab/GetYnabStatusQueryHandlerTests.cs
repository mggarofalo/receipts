using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Common;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabStatusQueryHandlerTests
{
	private readonly Mock<IYnabApiClient> _ynabClientMock = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock = new();
	private readonly Mock<IYnabSyncEventService> _syncEventMock = new();
	private readonly Mock<IYnabRateLimitTracker> _rateLimitMock = new();
	private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero));
	private readonly GetYnabStatusQueryHandler _handler;

	public GetYnabStatusQueryHandlerTests()
	{
		// Default the rate-limit tracker so handlers don't NRE when YNAB isn't configured.
		_rateLimitMock.Setup(r => r.GetStatus())
			.Returns(new YnabRateLimitStatus(200, 200, 0, null, null));
		_handler = new GetYnabStatusQueryHandler(
			_ynabClientMock.Object,
			_budgetSelectionMock.Object,
			_syncEventMock.Object,
			_rateLimitMock.Object,
			_timeProvider);
	}

	[Fact]
	public async Task Handle_NotConfigured_ReturnsAllZeroAndDisconnected()
	{
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		YnabStatusResult result = await _handler.Handle(new GetYnabStatusQuery(), CancellationToken.None);

		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.SelectedBudgetId.Should().BeNull();
		result.LastSuccessUtc.Should().BeNull();
		result.LastFailureUtc.Should().BeNull();
		result.Pushes24h.Should().Be(0);
		result.Pushes7d.Should().Be(0);
		result.Pushes30d.Should().Be(0);
		// Sync-event service should not be queried when not configured.
		_syncEventMock.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task Handle_Configured_AggregatesRollingWindows()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_budgetSelectionMock.Setup(b => b.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("b1");

		DateTimeOffset lastSuccess = _timeProvider.GetUtcNow().AddMinutes(-30);
		DateTimeOffset lastFailure = _timeProvider.GetUtcNow().AddHours(-3);
		_syncEventMock.Setup(s => s.GetLatestOccurrenceAsync(YnabSyncStatus.Synced, It.IsAny<CancellationToken>()))
			.ReturnsAsync(lastSuccess);
		_syncEventMock.Setup(s => s.GetLatestOccurrenceAsync(YnabSyncStatus.Failed, It.IsAny<CancellationToken>()))
			.ReturnsAsync(lastFailure);

		// 24h window: 10 total, 8 success, 2 failure
		_syncEventMock.Setup(s => s.CountSinceAsync(It.Is<DateTimeOffset>(d => d == _timeProvider.GetUtcNow().AddHours(-24)), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(10);
		_syncEventMock.Setup(s => s.CountSinceAsync(It.Is<DateTimeOffset>(d => d == _timeProvider.GetUtcNow().AddHours(-24)), YnabSyncStatus.Synced, It.IsAny<CancellationToken>()))
			.ReturnsAsync(8);
		_syncEventMock.Setup(s => s.CountSinceAsync(It.Is<DateTimeOffset>(d => d == _timeProvider.GetUtcNow().AddHours(-24)), YnabSyncStatus.Failed, It.IsAny<CancellationToken>()))
			.ReturnsAsync(2);

		_syncEventMock.Setup(s => s.CountSinceAsync(It.Is<DateTimeOffset>(d => d == _timeProvider.GetUtcNow().AddDays(-7)), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(50);
		_syncEventMock.Setup(s => s.CountSinceAsync(It.Is<DateTimeOffset>(d => d == _timeProvider.GetUtcNow().AddDays(-30)), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(120);

		// Act
		YnabStatusResult result = await _handler.Handle(new GetYnabStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.SelectedBudgetId.Should().Be("b1");
		result.LastSuccessUtc.Should().Be(lastSuccess);
		result.LastFailureUtc.Should().Be(lastFailure);
		result.Pushes24h.Should().Be(10);
		result.Successes24h.Should().Be(8);
		result.Failures24h.Should().Be(2);
		result.Pushes7d.Should().Be(50);
		result.Pushes30d.Should().Be(120);
	}

	[Fact]
	public async Task Handle_ConfiguredButBudgetsCallThrows_ReturnsDisconnectedButStillAggregates()
	{
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB unreachable"));

		YnabStatusResult result = await _handler.Handle(new GetYnabStatusQuery(), CancellationToken.None);

		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeFalse();
		// Event counts still queried — the rolling history is independent of live connectivity.
		_syncEventMock.Verify(s => s.CountSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<YnabSyncStatus?>(), It.IsAny<CancellationToken>()), Times.AtLeast(3));
	}
}
