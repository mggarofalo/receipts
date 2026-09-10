using Application.Commands.Ynab.PushTransactions;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Mediator;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class BulkPushYnabTransactionsCommandHandlerTests
{
	private readonly Mock<IMediator> _mediatorMock = new();
	private readonly Mock<IYnabRateLimitTracker> _rateLimitTrackerMock = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock = new();
	private readonly BulkPushYnabTransactionsCommandHandler _handler;

	public BulkPushYnabTransactionsCommandHandlerTests()
	{
		_rateLimitTrackerMock.Setup(r => r.CanMakeRequests(It.IsAny<int>())).Returns(true);
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-123");
		_handler = new BulkPushYnabTransactionsCommandHandler(
			_mediatorMock.Object,
			_rateLimitTrackerMock.Object,
			_budgetSelectionMock.Object);
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

		// Assert -- checks with 2 receipts * 2 estimated requests per receipt = 4
		_rateLimitTrackerMock.Verify(r => r.CanMakeRequests(4), Times.Once);
	}

	[Fact]
	public async Task Handle_AllSucceed_ReturnsAllResults()
	{
		// Arrange
		Guid receipt1 = Guid.NewGuid();
		Guid receipt2 = Guid.NewGuid();
		PushYnabTransactionsResult successResult1 = new(true, [new PushedTransactionInfo(Guid.NewGuid(), "ynab-1", -1000, 1)]);
		PushYnabTransactionsResult successResult2 = new(true, [new PushedTransactionInfo(Guid.NewGuid(), "ynab-2", -2000, 1)]);

		_mediatorMock.Setup(m => m.Send(It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receipt1), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult1);
		_mediatorMock.Setup(m => m.Send(It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receipt2), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult2);

		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand([receipt1, receipt2]), CancellationToken.None);

		// Assert
		result.Results.Should().HaveCount(2);
		result.Results[0].ReceiptId.Should().Be(receipt1);
		result.Results[0].Result.Success.Should().BeTrue();
		result.Results[1].ReceiptId.Should().Be(receipt2);
		result.Results[1].Result.Success.Should().BeTrue();
	}

	[Fact]
	public async Task Handle_ExceptionOnSecondReceipt_ContinuesAndReturnsAll()
	{
		// Bug 2: Exception on receipt N must not abort remaining receipts
		Guid receipt1 = Guid.NewGuid();
		Guid receipt2 = Guid.NewGuid();
		Guid receipt3 = Guid.NewGuid();

		PushYnabTransactionsResult successResult = new(true, [new PushedTransactionInfo(Guid.NewGuid(), "ynab-1", -1000, 1)]);

		_mediatorMock.Setup(m => m.Send(It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receipt1), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult);
		_mediatorMock.Setup(m => m.Send(It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receipt2), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("DB connection lost"));
		_mediatorMock.Setup(m => m.Send(It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receipt3), It.IsAny<CancellationToken>()))
			.ReturnsAsync(successResult);

		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand([receipt1, receipt2, receipt3]), CancellationToken.None);

		// Assert: all 3 receipts have results, receipt2 has failure
		result.Results.Should().HaveCount(3);
		result.Results[0].Result.Success.Should().BeTrue();
		result.Results[1].Result.Success.Should().BeFalse();
		result.Results[1].Result.Error.Should().Contain("DB connection lost");
		result.Results[2].Result.Success.Should().BeTrue();
	}

	[Fact]
	public async Task Handle_ExceptionResult_ContainsUnexpectedErrorPrefix()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("Network timeout"));

		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand([receiptId]), CancellationToken.None);

		// Assert
		result.Results.Should().HaveCount(1);
		result.Results[0].Result.Success.Should().BeFalse();
		result.Results[0].Result.Error.Should().StartWith("Unexpected error:");
		result.Results[0].Result.PushedTransactions.Should().BeEmpty();
	}

	[Fact]
	public async Task Handle_EmptyReceiptIds_ReturnsEmptyResults()
	{
		// Act
		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand([]), CancellationToken.None);

		// Assert
		result.Results.Should().BeEmpty();
		_mediatorMock.Verify(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_CapturesSelectedBudgetOnceForEveryDispatchedReceipt()
	{
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		_budgetSelectionMock.SetupSequence(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-A")
			.ReturnsAsync("budget-B");
		List<PushYnabTransactionsCommand> dispatched = [];
		_mediatorMock.Setup(m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()))
			.Callback((IRequest<PushYnabTransactionsResult> command, CancellationToken _) =>
				dispatched.Add((PushYnabTransactionsCommand)command))
			.ReturnsAsync(new PushYnabTransactionsResult(true, []));

		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand(receiptIds), CancellationToken.None);

		result.Results.Should().HaveCount(3);
		dispatched.Select(command => command.ReceiptId).Should().Equal(receiptIds);
		dispatched.Should().OnlyContain(command => command.CapturedYnabBudgetId == "budget-A");
		_budgetSelectionMock.Verify(
			s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WithoutSelectedBudget_FailsEveryReceiptWithoutDispatching()
	{
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid()];
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		BulkPushYnabTransactionsResult result = await _handler.Handle(
			new BulkPushYnabTransactionsCommand(receiptIds), CancellationToken.None);

		result.Results.Should().HaveCount(2);
		result.Results.Should().OnlyContain(item =>
			!item.Result.Success && item.Result.Error == "No YNAB budget selected.");
		_mediatorMock.Verify(
			m => m.Send(It.IsAny<PushYnabTransactionsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
