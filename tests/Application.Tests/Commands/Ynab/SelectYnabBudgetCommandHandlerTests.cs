using Application.Commands.Ynab.SelectBudget;
using Application.Interfaces.Services;
using FluentAssertions;
using Mediator;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class SelectYnabBudgetCommandHandlerTests
{
	[Fact]
	public async Task Handle_CallsSetSelectedBudgetIdAsync_WithCorrectBudgetId()
	{
		// Arrange
		Mock<IYnabBudgetSelectionService> mockService = new();
		SelectYnabBudgetCommandHandler handler = new(mockService.Object);
		string budgetId = Guid.NewGuid().ToString();

		// Act
		Unit result = await handler.Handle(new SelectYnabBudgetCommand(budgetId), CancellationToken.None);

		// Assert
		result.Should().Be(Unit.Value);
		mockService.Verify(s => s.SetSelectedBudgetIdAsync(budgetId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CanonicalizesUuidBudgetIdBeforePersistingSelection()
	{
		Mock<IYnabBudgetSelectionService> mockService = new();
		SelectYnabBudgetCommandHandler handler = new(mockService.Object);
		Guid budgetId = Guid.NewGuid();

		await handler.Handle(
			new SelectYnabBudgetCommand($"  {{{budgetId.ToString("D").ToUpperInvariant()}}}  "),
			CancellationToken.None);

		mockService.Verify(s => s.SetSelectedBudgetIdAsync(
			budgetId.ToString("D"),
			It.IsAny<CancellationToken>()), Times.Once);
	}
}
