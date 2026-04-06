using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetStaleMappingsQueryHandlerTests
{
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock = new();
	private readonly Mock<IYnabAccountMappingService> _accountMappingMock = new();
	private readonly Mock<IYnabCategoryMappingService> _categoryMappingMock = new();
	private readonly GetStaleMappingsQueryHandler _handler;

	public GetStaleMappingsQueryHandlerTests()
	{
		_handler = new GetStaleMappingsQueryHandler(
			_budgetSelectionMock.Object,
			_accountMappingMock.Object,
			_categoryMappingMock.Object);
	}

	[Fact]
	public async Task Handle_ReturnsStaleCounts_WhenBudgetSelected()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgetId);
		_accountMappingMock.Setup(s => s.CountStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(3);
		_categoryMappingMock.Setup(s => s.CountStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(5);

		// Act
		StaleMappingsResult result = await _handler.Handle(new GetStaleMappingsQuery(), CancellationToken.None);

		// Assert
		result.StaleAccountMappingCount.Should().Be(3);
		result.StaleCategoryMappingCount.Should().Be(5);
		result.CurrentBudgetId.Should().Be(budgetId);
	}

	[Fact]
	public async Task Handle_ReturnsZeroCounts_WhenNoBudgetSelected()
	{
		// Arrange
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		// Act
		StaleMappingsResult result = await _handler.Handle(new GetStaleMappingsQuery(), CancellationToken.None);

		// Assert
		result.StaleAccountMappingCount.Should().Be(0);
		result.StaleCategoryMappingCount.Should().Be(0);
		result.CurrentBudgetId.Should().BeNull();
		_accountMappingMock.Verify(s => s.CountStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		_categoryMappingMock.Verify(s => s.CountStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReturnsZeroCounts_WhenBudgetIdIsEmpty()
	{
		// Arrange
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(string.Empty);

		// Act
		StaleMappingsResult result = await _handler.Handle(new GetStaleMappingsQuery(), CancellationToken.None);

		// Assert
		result.StaleAccountMappingCount.Should().Be(0);
		result.StaleCategoryMappingCount.Should().Be(0);
		_accountMappingMock.Verify(s => s.CountStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReturnsZeroStaleCounts_WhenAllMappingsMatchBudget()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgetId);
		_accountMappingMock.Setup(s => s.CountStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_categoryMappingMock.Setup(s => s.CountStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		// Act
		StaleMappingsResult result = await _handler.Handle(new GetStaleMappingsQuery(), CancellationToken.None);

		// Assert
		result.StaleAccountMappingCount.Should().Be(0);
		result.StaleCategoryMappingCount.Should().Be(0);
		result.CurrentBudgetId.Should().Be(budgetId);
	}
}
