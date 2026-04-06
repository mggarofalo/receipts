using Application.Commands.Ynab.CategoryMapping;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class CreateYnabCategoryMappingCommandHandlerTests
{
	private readonly Mock<IYnabCategoryMappingService> _mockService = new();
	private readonly CreateYnabCategoryMappingCommandHandler _handler;

	public CreateYnabCategoryMappingCommandHandlerTests()
	{
		_handler = new CreateYnabCategoryMappingCommandHandler(_mockService.Object);
	}

	[Fact]
	public async Task Handle_CreatesMapping_WhenNoDuplicate()
	{
		// Arrange
		CreateYnabCategoryMappingCommand command = new(
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1");

		_mockService.Setup(s => s.GetByReceiptsCategoryAsync("Groceries", It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		YnabCategoryMappingDto expected = new(
			Guid.NewGuid(),
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1",
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Immediate Obligations", "budget-1",
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		// Act
		YnabCategoryMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(expected);
		_mockService.Verify(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Immediate Obligations", "budget-1",
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ThrowsInvalidOperationException_WhenDuplicateExists()
	{
		// Arrange
		CreateYnabCategoryMappingCommand command = new(
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1");

		YnabCategoryMappingDto existing = new(
			Guid.NewGuid(),
			"Groceries",
			"cat-456",
			"Food",
			"Needs",
			"budget-1",
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.GetByReceiptsCategoryAsync("Groceries", It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);

		// Act
		Func<Task> act = () => _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*already exists*");
	}

	[Fact]
	public async Task Handle_IsCaseSensitive_DifferentCaseIsNotDuplicate()
	{
		// Arrange
		CreateYnabCategoryMappingCommand command = new(
			"groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1");

		// GetByReceiptsCategoryAsync for "groceries" returns null (no exact match)
		_mockService.Setup(s => s.GetByReceiptsCategoryAsync("groceries", It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		YnabCategoryMappingDto expected = new(
			Guid.NewGuid(),
			"groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			"budget-1",
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.CreateAsync(
			"groceries", "cat-123", "Groceries", "Immediate Obligations", "budget-1",
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		// Act
		YnabCategoryMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.ReceiptsCategory.Should().Be("groceries");
	}
}
