using Application.Commands.Ynab.CategoryMapping;
using Application.Interfaces.Services;
using MediatR;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class UpdateYnabCategoryMappingCommandHandlerTests
{
	[Fact]
	public async Task Handle_CallsUpdateAsync_WithCorrectParameters()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		UpdateYnabCategoryMappingCommandHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		UpdateYnabCategoryMappingCommand command = new(
			id,
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1");

		// Act
		Unit result = await handler.Handle(command, CancellationToken.None);

		// Assert
		Assert.Equal(Unit.Value, result);
		mockService.Verify(s => s.UpdateAsync(
			id, "cat-123", "Groceries", "Immediate Obligations", "budget-1",
			It.IsAny<CancellationToken>()), Times.Once);
	}
}
