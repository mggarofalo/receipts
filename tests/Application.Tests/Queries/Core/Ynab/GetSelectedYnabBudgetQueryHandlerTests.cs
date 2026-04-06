using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetSelectedYnabBudgetQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsBudgetSelection_WithSelectedId()
	{
		// Arrange
		string expectedId = Guid.NewGuid().ToString();
		Mock<IYnabBudgetSelectionService> mockService = new();
		mockService.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedId);

		GetSelectedYnabBudgetQueryHandler handler = new(mockService.Object);

		// Act
		YnabBudgetSelection result = await handler.Handle(new GetSelectedYnabBudgetQuery(), CancellationToken.None);

		// Assert
		result.SelectedBudgetId.Should().Be(expectedId);
	}

	[Fact]
	public async Task Handle_ReturnsBudgetSelection_WithNullId_WhenNoneSelected()
	{
		// Arrange
		Mock<IYnabBudgetSelectionService> mockService = new();
		mockService.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		GetSelectedYnabBudgetQueryHandler handler = new(mockService.Object);

		// Act
		YnabBudgetSelection result = await handler.Handle(new GetSelectedYnabBudgetQuery(), CancellationToken.None);

		// Assert
		result.SelectedBudgetId.Should().BeNull();
	}
}
