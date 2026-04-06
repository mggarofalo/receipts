using Application.Commands.Ynab.StaleMappings;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class ClearStaleMappingsCommandHandlerTests
{
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock = new();
	private readonly Mock<IYnabAccountMappingService> _accountMappingMock = new();
	private readonly Mock<IYnabCategoryMappingService> _categoryMappingMock = new();
	private readonly ClearStaleMappingsCommandHandler _handler;

	public ClearStaleMappingsCommandHandlerTests()
	{
		_handler = new ClearStaleMappingsCommandHandler(
			_budgetSelectionMock.Object,
			_accountMappingMock.Object,
			_categoryMappingMock.Object);
	}

	[Fact]
	public async Task Handle_DeletesStaleMappings_WhenBudgetSelected()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgetId);
		_accountMappingMock.Setup(s => s.DeleteStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(2);
		_categoryMappingMock.Setup(s => s.DeleteStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(4);

		// Act
		ClearStaleMappingsResult result = await _handler.Handle(new ClearStaleMappingsCommand(), CancellationToken.None);

		// Assert
		result.DeletedAccountMappings.Should().Be(2);
		result.DeletedCategoryMappings.Should().Be(4);
	}

	[Fact]
	public async Task Handle_ReturnsZeros_WhenNoBudgetSelected()
	{
		// Arrange
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		// Act
		ClearStaleMappingsResult result = await _handler.Handle(new ClearStaleMappingsCommand(), CancellationToken.None);

		// Assert
		result.DeletedAccountMappings.Should().Be(0);
		result.DeletedCategoryMappings.Should().Be(0);
		_accountMappingMock.Verify(s => s.DeleteStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		_categoryMappingMock.Verify(s => s.DeleteStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReturnsZeros_WhenBudgetIdIsEmpty()
	{
		// Arrange
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(string.Empty);

		// Act
		ClearStaleMappingsResult result = await _handler.Handle(new ClearStaleMappingsCommand(), CancellationToken.None);

		// Assert
		result.DeletedAccountMappings.Should().Be(0);
		result.DeletedCategoryMappings.Should().Be(0);
		_accountMappingMock.Verify(s => s.DeleteStaleMappingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReturnsZeros_WhenNoStaleMappingsExist()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgetId);
		_accountMappingMock.Setup(s => s.DeleteStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_categoryMappingMock.Setup(s => s.DeleteStaleMappingsAsync(budgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		// Act
		ClearStaleMappingsResult result = await _handler.Handle(new ClearStaleMappingsCommand(), CancellationToken.None);

		// Assert
		result.DeletedAccountMappings.Should().Be(0);
		result.DeletedCategoryMappings.Should().Be(0);
	}
}
