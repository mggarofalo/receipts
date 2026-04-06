using Application.Commands.Ynab.MemoSync;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class ResolveYnabMemoSyncCommandHandlerTests
{
	private readonly Mock<IYnabMemoSyncService> _memoSyncServiceMock = new();
	private readonly ResolveYnabMemoSyncCommandHandler _handler;

	public ResolveYnabMemoSyncCommandHandlerTests()
	{
		_handler = new ResolveYnabMemoSyncCommandHandler(_memoSyncServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		Guid localTransactionId = Guid.NewGuid();
		string ynabTransactionId = "yt-1";
		YnabMemoSyncResult expected = new(localTransactionId, Guid.NewGuid(), YnabMemoSyncOutcome.Synced, ynabTransactionId, null, null);

		_memoSyncServiceMock
			.Setup(s => s.ResolveMemoSyncAsync(localTransactionId, ynabTransactionId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		ResolveYnabMemoSyncCommand command = new(localTransactionId, ynabTransactionId);

		// Act
		YnabMemoSyncResult result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_memoSyncServiceMock.Verify(s => s.ResolveMemoSyncAsync(localTransactionId, ynabTransactionId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
