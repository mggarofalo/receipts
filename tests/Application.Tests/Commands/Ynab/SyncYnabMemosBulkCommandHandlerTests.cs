using Application.Commands.Ynab.MemoSync;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class SyncYnabMemosBulkCommandHandlerTests
{
	private readonly Mock<IYnabMemoSyncService> _memoSyncServiceMock = new();
	private readonly SyncYnabMemosBulkCommandHandler _handler;

	public SyncYnabMemosBulkCommandHandlerTests()
	{
		_handler = new SyncYnabMemosBulkCommandHandler(_memoSyncServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		List<Guid> receiptIds = [Guid.NewGuid(), Guid.NewGuid()];
		List<YnabMemoSyncResult> expected =
		[
			new(Guid.NewGuid(), receiptIds[0], YnabMemoSyncOutcome.Synced, "yt-1", null, null),
			new(Guid.NewGuid(), receiptIds[1], YnabMemoSyncOutcome.NoMatch, null, null, null),
		];

		_memoSyncServiceMock
			.Setup(s => s.SyncMemosBulkAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		SyncYnabMemosBulkCommand command = new(receiptIds);

		// Act
		List<YnabMemoSyncResult> result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_memoSyncServiceMock.Verify(s => s.SyncMemosBulkAsync(receiptIds, It.IsAny<CancellationToken>()), Times.Once);
	}
}
