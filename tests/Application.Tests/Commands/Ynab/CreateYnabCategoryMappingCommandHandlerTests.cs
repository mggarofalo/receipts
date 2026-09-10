using Application.Commands.Ynab.CategoryMapping;
using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class CreateYnabCategoryMappingCommandHandlerTests
{
	private const string BudgetId = "11111111-1111-1111-1111-111111111111";
	private readonly Mock<IYnabCategoryMappingService> _mockService = new();
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelection = new();
	private readonly CreateYnabCategoryMappingCommandHandler _handler;

	public CreateYnabCategoryMappingCommandHandlerTests()
	{
		_budgetSelection.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(BudgetId);
		_handler = new CreateYnabCategoryMappingCommandHandler(_mockService.Object, _budgetSelection.Object);
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
			BudgetId);

		_mockService.Setup(s => s.GetByReceiptsCategoryAndBudgetAsync("Groceries", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		YnabCategoryMappingDto expected = new(
			Guid.NewGuid(),
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			BudgetId,
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Immediate Obligations", BudgetId,
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		// Act
		YnabCategoryMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(expected);
		_mockService.Verify(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Immediate Obligations", BudgetId,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ThrowsDuplicateEntityException_WhenDuplicateExists()
	{
		// Arrange
		CreateYnabCategoryMappingCommand command = new(
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			BudgetId);

		YnabCategoryMappingDto existing = new(
			Guid.NewGuid(),
			"Groceries",
			"cat-456",
			"Food",
			"Needs",
			BudgetId,
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.GetByReceiptsCategoryAndBudgetAsync("Groceries", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<DuplicateEntityException>()
			.WithMessage("*already exists*");
	}

	[Fact]
	public async Task Handle_PropagatesDuplicateEntityException_FromServiceToctouRace()
	{
		// Arrange: simulate TOCTOU race — existence check passes but CreateAsync
		// hits a unique constraint and the service converts it to DuplicateEntityException
		CreateYnabCategoryMappingCommand command = new(
			"Groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			BudgetId);

		_mockService.Setup(s => s.GetByReceiptsCategoryAndBudgetAsync("Groceries", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		_mockService.Setup(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Immediate Obligations", BudgetId,
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateEntityException("A mapping for receipts category 'Groceries' already exists."));

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<DuplicateEntityException>()
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
			BudgetId);

		// The current budget has no exact-case match for "groceries".
		_mockService.Setup(s => s.GetByReceiptsCategoryAndBudgetAsync("groceries", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		YnabCategoryMappingDto expected = new(
			Guid.NewGuid(),
			"groceries",
			"cat-123",
			"Groceries",
			"Immediate Obligations",
			BudgetId,
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mockService.Setup(s => s.CreateAsync(
			"groceries", "cat-123", "Groceries", "Immediate Obligations", BudgetId,
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		// Act
		YnabCategoryMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.ReceiptsCategory.Should().Be("groceries");
	}

	[Fact]
	public async Task Handle_CanonicalizesRequestBudgetForComparisonLookupAndPersistence()
	{
		string nonCanonicalBudgetId = $"  {{{BudgetId.ToUpperInvariant()}}}  ";
		_mockService.Setup(s => s.GetByReceiptsCategoryAndBudgetAsync(
			"Groceries", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);
		YnabCategoryMappingDto expected = new(
			Guid.NewGuid(), "Groceries", "cat-123", "Groceries", "Needs", BudgetId,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		_mockService.Setup(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Needs", BudgetId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		YnabCategoryMappingDto result = await _handler.Handle(
			new CreateYnabCategoryMappingCommand(
				"Groceries", "cat-123", "Groceries", "Needs", nonCanonicalBudgetId),
			CancellationToken.None);

		result.Should().Be(expected);
		_mockService.Verify(s => s.CreateAsync(
			"Groceries", "cat-123", "Groceries", "Needs", BudgetId, It.IsAny<CancellationToken>()), Times.Once);
	}
}
