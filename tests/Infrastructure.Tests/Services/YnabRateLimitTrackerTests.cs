using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Ynab;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.Tests.Services;

public class YnabRateLimitTrackerTests
{
	private static (YnabRateLimitTracker Tracker, FakeTimeProvider TimeProvider) CreateTracker(
		int maxRequests = 200, int windowSeconds = 3600)
	{
		YnabClientOptions options = new()
		{
			RateLimitMaxRequests = maxRequests,
			RateLimitWindowSeconds = windowSeconds,
		};
		FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
		YnabRateLimitTracker tracker = new(options, timeProvider);
		return (tracker, timeProvider);
	}

	[Fact]
	public void GetStatus_NoRequests_ReturnsFullQuota()
	{
		// Arrange
		(YnabRateLimitTracker tracker, _) = CreateTracker();

		// Act
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert
		status.RemainingRequests.Should().Be(200);
		status.MaxRequests.Should().Be(200);
		status.RequestsUsed.Should().Be(0);
		status.OldestRequestAt.Should().BeNull();
	}

	[Fact]
	public void RecordRequest_DecrementsRemaining()
	{
		// Arrange
		(YnabRateLimitTracker tracker, _) = CreateTracker();

		// Act
		tracker.RecordRequest();
		tracker.RecordRequest();
		tracker.RecordRequest();
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert
		status.RemainingRequests.Should().Be(197);
		status.RequestsUsed.Should().Be(3);
		status.OldestRequestAt.Should().NotBeNull();
	}

	[Fact]
	public void CanMakeRequests_WithinLimit_ReturnsTrue()
	{
		// Arrange
		(YnabRateLimitTracker tracker, _) = CreateTracker(maxRequests: 10);

		for (int i = 0; i < 5; i++)
		{
			tracker.RecordRequest();
		}

		// Act & Assert
		tracker.CanMakeRequests(5).Should().BeTrue();
		tracker.CanMakeRequests(6).Should().BeFalse();
	}

	[Fact]
	public void CanMakeRequests_ExceedsLimit_ReturnsFalse()
	{
		// Arrange
		(YnabRateLimitTracker tracker, _) = CreateTracker(maxRequests: 5);

		for (int i = 0; i < 5; i++)
		{
			tracker.RecordRequest();
		}

		// Act & Assert
		tracker.CanMakeRequests(1).Should().BeFalse();
	}

	[Fact]
	public void ExpiredRequests_ArePruned()
	{
		// Arrange
		(YnabRateLimitTracker tracker, FakeTimeProvider timeProvider) = CreateTracker(maxRequests: 200, windowSeconds: 3600);

		// Record 10 requests
		for (int i = 0; i < 10; i++)
		{
			tracker.RecordRequest();
		}

		tracker.GetStatus().RequestsUsed.Should().Be(10);

		// Advance time past the window
		timeProvider.Advance(TimeSpan.FromSeconds(3601));

		// Act
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert — all expired, back to full quota
		status.RemainingRequests.Should().Be(200);
		status.RequestsUsed.Should().Be(0);
		status.OldestRequestAt.Should().BeNull();
	}

	[Fact]
	public void SlidingWindow_PartialExpiry()
	{
		// Arrange
		(YnabRateLimitTracker tracker, FakeTimeProvider timeProvider) = CreateTracker(maxRequests: 200, windowSeconds: 3600);

		// Record 5 requests at T=0
		for (int i = 0; i < 5; i++)
		{
			tracker.RecordRequest();
		}

		// Advance 30 minutes
		timeProvider.Advance(TimeSpan.FromMinutes(30));

		// Record 3 more requests at T=30min
		for (int i = 0; i < 3; i++)
		{
			tracker.RecordRequest();
		}

		tracker.GetStatus().RequestsUsed.Should().Be(8);

		// Advance to T=61min (just past 1hr from first batch)
		timeProvider.Advance(TimeSpan.FromMinutes(31));

		// Act
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert — first 5 expired, 3 remain
		status.RequestsUsed.Should().Be(3);
		status.RemainingRequests.Should().Be(197);
	}

	[Fact]
	public void WindowResetAt_ReflectsOldestRequest()
	{
		// Arrange
		(YnabRateLimitTracker tracker, FakeTimeProvider timeProvider) = CreateTracker(windowSeconds: 3600);
		DateTimeOffset recordTime = timeProvider.GetUtcNow();

		tracker.RecordRequest();

		// Act
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert — window resets when oldest request + window duration
		status.WindowResetAt.Should().BeCloseTo(recordTime.AddSeconds(3600), TimeSpan.FromSeconds(1));
		status.OldestRequestAt.Should().BeCloseTo(recordTime, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void RemainingRequests_NeverNegative()
	{
		// Arrange — small limit, record more than max
		(YnabRateLimitTracker tracker, _) = CreateTracker(maxRequests: 3);

		for (int i = 0; i < 5; i++)
		{
			tracker.RecordRequest();
		}

		// Act
		YnabRateLimitStatus status = tracker.GetStatus();

		// Assert
		status.RemainingRequests.Should().Be(0);
		status.RequestsUsed.Should().Be(5);
	}
}
