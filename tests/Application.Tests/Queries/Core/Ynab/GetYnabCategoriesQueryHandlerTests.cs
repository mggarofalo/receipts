using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabCategoriesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsCategoriesFromApiClient()
	{
		// Arrange
		Mock<IYnabApiClient> mockApiClient = new();
		GetYnabCategoriesQueryHandler handler = new(mockApiClient.Object);
		string budgetId = "budget-1";

		List<YnabCategory> categories =
		[
			new("cat-1", "Groceries", "group-1", "Immediate Obligations", false),
			new("cat-2", "Rent", "group-1", "Immediate Obligations", false),
		];

		mockApiClient.Setup(c => c.GetCategoriesAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(categories);

		// Act
		List<YnabCategory> result = await handler.Handle(new GetYnabCategoriesQuery(budgetId), CancellationToken.None);

		// Assert
		result.Should().HaveCount(2);
		result.Should().BeEquivalentTo(categories);
	}
}
