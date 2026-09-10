using Application.Commands.Ynab.CategoryMapping;
using Application.Interfaces.Services;
using Mediator;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class UpdateYnabCategoryMappingCommandHandlerTests
{
	private const string BudgetId = "11111111-1111-1111-1111-111111111111";

	[Fact]
	public async Task Handle_CallsUpdateAsync_WithCorrectParameters()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(BudgetId);
		UpdateYnabCategoryMappingCommandHandler handler = new(mockService.Object, budgetSelection.Object);
		Guid id = Guid.NewGuid();

		UpdateYnabCategoryMappingCommand command = new(
			id,
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			BudgetId);

		// Act
		Unit result = await handler.Handle(command, CancellationToken.None);

		// Assert
		Assert.Equal(Unit.Value, result);
		mockService.Verify(s => s.UpdateAsync(
			id, "cat-123", "Groceries", "Immediate Obligations", BudgetId,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CanonicalizesRequestBudgetForComparisonAndPersistence()
	{
		Mock<IYnabCategoryMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(BudgetId);
		UpdateYnabCategoryMappingCommandHandler handler = new(mockService.Object, budgetSelection.Object);
		Guid id = Guid.NewGuid();

		await handler.Handle(
			new UpdateYnabCategoryMappingCommand(
				id, "cat-123", "Groceries", "Needs", $"  {{{BudgetId.ToUpperInvariant()}}}  "),
			CancellationToken.None);

		mockService.Verify(s => s.UpdateAsync(
			id, "cat-123", "Groceries", "Needs", BudgetId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
