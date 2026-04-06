using Application.Interfaces.Services;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetDistinctReceiptItemCategoriesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsDistinctCategories()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		GetDistinctReceiptItemCategoriesQueryHandler handler = new(mockService.Object);

		List<string> categories = ["Electronics", "Groceries", "Pharmacy"];
		mockService.Setup(s => s.GetDistinctReceiptItemCategoriesAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(categories);

		// Act
		List<string> result = await handler.Handle(new GetDistinctReceiptItemCategoriesQuery(), CancellationToken.None);

		// Assert
		result.Should().BeEquivalentTo(categories);
	}
}
