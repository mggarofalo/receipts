using Application.Commands.Ynab.PushTransactions;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using MediatR;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class BulkPushYnabTransactionsCommandHandlerTests
{
	private readonly Mock<IMediator> _mediatorMock = new();
	private readonly Mock<IYnabRateLimitTracker> _rateLimitTrackerMock = new();
	private readonly BulkPushYnabTransactionsCommandHandler _handler;

	public BulkPushYnabTransactionsCommandHandlerTests()
	{
		_rateLimitTrackerMock.Setup(r => r.CanMakeRequests(It.IsAny<int>())).Returns(true);
		_handler = new BulkPushYnabTransactionsCommandHandler(_mediatorMock.Object, _rateLimitTrackerMock.Object);
	}

	[Fact]
	public async Task Handle_WithinRateLimit_ProcessesAllReceipts()
	{
		// Arrange
		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		List<Guid> receiptIds = [receiptId1, receiptId2];

		PushYnabTransactionsResult successResult = new(true, [new PushedTransactionInfo(Guid.NewGuid(), "yt-1", -10000, 0)]);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult);

		BulkPushYnabTransactionsCommand command = new(receiptIds);

		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Results.Should().HaveCount(2);
		result.Results.Should().AllSatisfy(r => r.Result.Success.Should().BeTrue());
		_mediatorMock.Verify(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task Handle_RateLimitExceeded_ReturnsFailedResults()
	{
		// Arrange
		_rateLimitTrackerMock.Setup(r => r.CanMakeRequests(It.IsAny<int>())).Returns(false);
		_rateLimitTrackerMock.Setup(r => r.GetStatus()).Returns(
			new YnabRateLimitStatus(5, 200, 195, DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddMinutes(-30)));

		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		BulkPushYnabTransactionsCommand command = new(receiptIds);

		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Results.Should().HaveCount(3);
		result.Results.Should().AllSatisfy(r =>
		{
			r.Result.Success.Should().BeFalse();
			r.Result.Error.Should().Contain("rate limit");
		});
		_mediatorMock.Verify(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ChecksRateLimitWithEstimatedRequests()
	{
		// Arrange
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid()];
		BulkPushYnabTransactionsCommand command = new(receiptIds);

		PushYnabTransactionsResult successResult = new(true, []);
		_mediatorMock
			.Setup(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult);

		// Act
		await _handler.Handle(command, CancellationToken.None);

		// Assert — checks with 2 receipts * 2 estimated requests per receipt = 4
		_rateLimitTrackerMock.Verify(r => r.CanMakeRequests(4), Times.Once);
	}
}
