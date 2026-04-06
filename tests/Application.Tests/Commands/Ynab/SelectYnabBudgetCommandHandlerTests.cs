using Application.Commands.Ynab.SelectBudget;
using Application.Interfaces.Services;
using FluentAssertions;
using MediatR;
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
}
