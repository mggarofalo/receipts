using Application.Commands.Ynab.CategoryMapping;
using Application.Interfaces.Services;
using MediatR;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class DeleteYnabCategoryMappingCommandHandlerTests
{
	[Fact]
	public async Task Handle_CallsDeleteAsync_WithCorrectId()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		DeleteYnabCategoryMappingCommandHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		// Act
		Unit result = await handler.Handle(new DeleteYnabCategoryMappingCommand(id), CancellationToken.None);

		// Assert
		Assert.Equal(Unit.Value, result);
		mockService.Verify(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}
}
