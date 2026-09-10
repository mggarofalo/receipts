using Application.Interfaces.Services;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetUnmappedCategoriesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsUnmappedCategories()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-B");
		GetUnmappedCategoriesQueryHandler handler = new(mockService.Object, budgetSelection.Object);

		List<string> unmapped = ["Electronics", "Pharmacy"];
		mockService.Setup(s => s.GetUnmappedCategoriesAsync("budget-B", It.IsAny<CancellationToken>()))
			.ReturnsAsync(unmapped);

		// Act
		List<string> result = await handler.Handle(new GetUnmappedCategoriesQuery(), CancellationToken.None);

		// Assert
		result.Should().BeEquivalentTo(unmapped);
	}
}
