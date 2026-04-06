using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabBudgetsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsBudgetsFromApiClient()
	{
		// Arrange
		List<YnabBudget> expected =
		[
			new("budget-1", "My Budget"),
			new("budget-2", "Other Budget"),
		];

		Mock<IYnabApiClient> mockClient = new();
		mockClient.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetYnabBudgetsQueryHandler handler = new(mockClient.Object);

		// Act
		List<YnabBudget> result = await handler.Handle(new GetYnabBudgetsQuery(), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
	}
}
