using Application.Commands.Trash.Purge;
using Application.Interfaces.Services;
using Moq;

namespace Application.Tests.Commands.Trash;

public class PurgeTrashCommandHandlerTests
{
	[Fact]
	public async Task Handle_CallsPurgeAllDeletedAsync()
	{
		Mock<ITrashService> mockService = new();
		PurgeTrashResult expected = new(2, 3, "History retained");
		mockService.Setup(s => s.PurgeAllDeletedAsync(CancellationToken.None)).ReturnsAsync(expected);
		PurgeTrashCommandHandler handler = new(mockService.Object);

		PurgeTrashCommand command = new();
		PurgeTrashResult result = await handler.Handle(command, CancellationToken.None);

		Assert.Equal(expected, result);
		mockService.Verify(s => s.PurgeAllDeletedAsync(CancellationToken.None), Times.Once);
	}
}
