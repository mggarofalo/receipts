using Application.Commands.Reports;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Reports;

public class AcceptDuplicateGroupCommandHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly AcceptDuplicateGroupCommandHandler _handler;

	public AcceptDuplicateGroupCommandHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new AcceptDuplicateGroupCommandHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid()];
		AcceptDuplicateGroupCommand command = new(receiptIds);

		_reportServiceMock.Setup(s => s.AcceptDuplicateGroupAsync(
			receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(1);
		_reportServiceMock.Verify(s => s.AcceptDuplicateGroupAsync(
			receiptIds, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsAcceptedPairCount()
	{
		// Arrange — three receipts produce C(3,2) = 3 pairs.
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		AcceptDuplicateGroupCommand command = new(receiptIds);

		_reportServiceMock.Setup(s => s.AcceptDuplicateGroupAsync(
			It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(3);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(3);
	}

	[Fact]
	public async Task Handle_ReturnsZero_WhenGroupWasAlreadyAccepted()
	{
		// Arrange — the service is idempotent, so a repeat acceptance adds no pairs.
		AcceptDuplicateGroupCommand command = new([Guid.NewGuid(), Guid.NewGuid()]);

		_reportServiceMock.Setup(s => s.AcceptDuplicateGroupAsync(
			It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(0);
	}

	[Fact]
	public async Task Handle_PropagatesKeyNotFound_WhenReceiptMissing()
	{
		// Arrange
		AcceptDuplicateGroupCommand command = new([Guid.NewGuid(), Guid.NewGuid()]);

		_reportServiceMock.Setup(s => s.AcceptDuplicateGroupAsync(
			It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt(s) not found"));

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert — the controller relies on this surfacing to translate it into a 404.
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}
}
