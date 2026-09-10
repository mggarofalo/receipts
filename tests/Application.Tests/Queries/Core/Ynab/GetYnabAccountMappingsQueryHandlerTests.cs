using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabAccountMappingsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsMappingsFromService()
	{
		// Arrange
		List<YnabAccountMappingDto> expected =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-2", "Savings", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		Mock<IYnabAccountMappingService> mockService = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-1");
		mockService.Setup(s => s.GetByBudgetIdAsync("budget-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetYnabAccountMappingsQueryHandler handler = new(mockService.Object, budgetSelection.Object);

		// Act
		List<YnabAccountMappingDto> result = await handler.Handle(new GetYnabAccountMappingsQuery(), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_NoSelectedBudget_ReturnsEmptyWithoutReadingHistoricalMappings()
	{
		Mock<IYnabAccountMappingService> service = new();
		Mock<IYnabBudgetSelectionService> budgetSelection = new();
		budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		GetYnabAccountMappingsQueryHandler handler = new(service.Object, budgetSelection.Object);

		List<YnabAccountMappingDto> result = await handler.Handle(
			new GetYnabAccountMappingsQuery(), CancellationToken.None);

		result.Should().BeEmpty();
		service.Verify(s => s.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
		service.Verify(s => s.GetByBudgetIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
