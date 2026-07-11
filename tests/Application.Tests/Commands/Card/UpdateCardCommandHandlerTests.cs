using Application.Commands.Card.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Card;

public class UpdateCardCommandHandlerTests
{
	[Fact]
	public async Task UpdateCardCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<ICardService> mockService = new();
		UpdateCardCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Card> input = CardGenerator.GenerateList(2);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.Card>>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateCardCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateCardCommandHandler_WithMissingCard_ReturnsFalseAndNeverPersists()
	{
		Mock<ICardService> mockService = new();
		UpdateCardCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Card> input = CardGenerator.GenerateList(1);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		UpdateCardCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.Card>>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}