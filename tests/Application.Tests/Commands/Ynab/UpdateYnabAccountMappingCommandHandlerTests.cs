using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using FluentAssertions;
using Mediator;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class UpdateYnabAccountMappingCommandHandlerTests
{
	private const string BudgetId = "11111111-1111-1111-1111-111111111111";

	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		Mock<IYnabAccountMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(BudgetId);
		UpdateYnabAccountMappingCommandHandler handler = new(mockService.Object, budgetSelection.Object);
		Guid id = Guid.NewGuid();

		UpdateYnabAccountMappingCommand command = new(id, "ynab-acc-2", "Savings", BudgetId);

		// Act
		Unit result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(Unit.Value);
		mockService.Verify(s => s.UpdateAsync(
			id, "ynab-acc-2", "Savings", BudgetId,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_CanonicalizesRequestBudgetForComparisonAndPersistence()
	{
		Mock<IYnabAccountMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(BudgetId);
		UpdateYnabAccountMappingCommandHandler handler = new(mockService.Object, budgetSelection.Object);
		Guid id = Guid.NewGuid();

		await handler.Handle(
			new UpdateYnabAccountMappingCommand(
				id, "ynab-acc-2", "Savings", $"  {{{BudgetId.ToUpperInvariant()}}}  "),
			CancellationToken.None);

		mockService.Verify(s => s.UpdateAsync(
			id, "ynab-acc-2", "Savings", BudgetId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
