using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabRateLimitStatusQueryHandlerTests
{
	private readonly Mock<IYnabRateLimitTracker> _trackerMock = new();
	private readonly GetYnabRateLimitStatusQueryHandler _handler;

	public GetYnabRateLimitStatusQueryHandlerTests()
	{
		_handler = new GetYnabRateLimitStatusQueryHandler(_trackerMock.Object);
	}

	[Fact]
	public async Task Handle_ReturnsTrackerStatus()
	{
		// Arrange
		YnabRateLimitStatus expected = new(
			RemainingRequests: 150,
			MaxRequests: 200,
			RequestsUsed: 50,
			WindowResetAt: DateTimeOffset.UtcNow.AddMinutes(30),
			OldestRequestAt: DateTimeOffset.UtcNow.AddMinutes(-30));

		_trackerMock.Setup(t => t.GetStatus()).Returns(expected);

		// Act
		YnabRateLimitStatus result = await _handler.Handle(new GetYnabRateLimitStatusQuery(), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_trackerMock.Verify(t => t.GetStatus(), Times.Once);
	}
}
