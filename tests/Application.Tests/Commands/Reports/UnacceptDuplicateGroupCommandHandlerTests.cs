using Application.Commands.Reports;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Reports;

public class UnacceptDuplicateGroupCommandHandlerTests
{
	private readonly Mock<IDuplicateAcceptanceStore> _storeMock;
	private readonly UnacceptDuplicateGroupCommandHandler _handler;

	public UnacceptDuplicateGroupCommandHandlerTests()
	{
		_storeMock = new Mock<IDuplicateAcceptanceStore>();
		_handler = new UnacceptDuplicateGroupCommandHandler(_storeMock.Object);
	}

	[Fact]
	public async Task Handle_RemovesOnlySubmittedPairs()
	{
		// Arrange
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid()];
		UnacceptDuplicateGroupCommand command = new(receiptIds);

		_storeMock.Setup(s => s.RemovePairsAsync(
			receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(1);
		_storeMock.Verify(s => s.RemovePairsAsync(
			receiptIds, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsRemovedPairCount()
	{
		// Arrange
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
		UnacceptDuplicateGroupCommand command = new(receiptIds);

		_storeMock.Setup(s => s.RemovePairsAsync(
			It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(3);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(3);
	}

	[Fact]
	public async Task Handle_ReturnsZero_WhenNothingWasAccepted()
	{
		// Arrange
		UnacceptDuplicateGroupCommand command = new([Guid.NewGuid(), Guid.NewGuid()]);

		_storeMock.Setup(s => s.RemovePairsAsync(
			It.IsAny<List<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		// Act
		int result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(0);
	}
}
