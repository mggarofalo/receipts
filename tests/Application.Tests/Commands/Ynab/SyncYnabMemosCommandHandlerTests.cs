using Application.Commands.Ynab.MemoSync;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class SyncYnabMemosCommandHandlerTests
{
	private readonly Mock<IYnabMemoSyncService> _memoSyncServiceMock = new();
	private readonly SyncYnabMemosCommandHandler _handler;

	public SyncYnabMemosCommandHandlerTests()
	{
		_handler = new SyncYnabMemosCommandHandler(_memoSyncServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<YnabMemoSyncResult> expected =
		[
			new(Guid.NewGuid(), receiptId, YnabMemoSyncOutcome.Synced, "yt-1", null, null),
		];

		_memoSyncServiceMock
			.Setup(s => s.SyncMemosByReceiptAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		SyncYnabMemosCommand command = new(receiptId);

		// Act
		List<YnabMemoSyncResult> result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_memoSyncServiceMock.Verify(s => s.SyncMemosByReceiptAsync(receiptId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
