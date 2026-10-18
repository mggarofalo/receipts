using Domain.NormalizedDescriptions;
using FluentAssertions;

namespace Domain.Tests.NormalizedDescriptions;

public class NormalizedDescriptionSettingsTests
{
	private static readonly DateTimeOffset NowTimestamp = new(2026, 4, 19, 0, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(double.NaN, 0.5)]
	[InlineData(0.9, double.NaN)]
	[InlineData(double.PositiveInfinity, 0.5)]
	[InlineData(0.9, double.NegativeInfinity)]
	public void Validate_NonfiniteThreshold_Rejects(double autoAccept, double pendingReview)
	{
		Action act = () => NormalizedDescriptionSettings.Validate(autoAccept, pendingReview);
		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructor_ValidThresholds_AssignsProperties()
	{
		Guid id = Guid.NewGuid();
		NormalizedDescriptionSettings settings = new(id, autoAcceptThreshold: 0.9, pendingReviewThreshold: 0.5, updatedAt: NowTimestamp);

		settings.Id.Should().Be(id);
		settings.AutoAcceptThreshold.Should().Be(0.9);
		settings.PendingReviewThreshold.Should().Be(0.5);
		settings.UpdatedAt.Should().Be(NowTimestamp);
	}

	[Theory]
	[InlineData(0.0, 0.0)] // pending == auto — pending must be strictly less
	[InlineData(1.0, 1.0)]
	[InlineData(0.5, 0.8)] // pending > auto
	[InlineData(0.81, 0.81)]
	public void Constructor_PendingNotStrictlyLessThanAuto_Throws(double autoAccept, double pendingReview)
	{
		Action act = () => _ = new NormalizedDescriptionSettings(Guid.NewGuid(), autoAccept, pendingReview, NowTimestamp);
		act.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData(-0.01, 0.5)]
	[InlineData(1.01, 0.5)]
	[InlineData(0.8, -0.01)]
	[InlineData(0.8, 1.01)]
	public void Constructor_ThresholdOutOfRange_Throws(double autoAccept, double pendingReview)
	{
		Action act = () => _ = new NormalizedDescriptionSettings(Guid.NewGuid(), autoAccept, pendingReview, NowTimestamp);
		act.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData(0.81, 0.68)] // the canonical baseline
	[InlineData(1.0, 0.0)]   // boundary values both allowed
	[InlineData(0.5, 0.499)] // tight spread
	public void Validate_ValidPairs_DoesNotThrow(double autoAccept, double pendingReview)
	{
		Action act = () => NormalizedDescriptionSettings.Validate(autoAccept, pendingReview);
		act.Should().NotThrow();
	}
}
