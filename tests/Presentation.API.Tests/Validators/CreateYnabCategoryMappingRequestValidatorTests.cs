using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class CreateYnabCategoryMappingRequestValidatorTests
{
	private readonly CreateYnabCategoryMappingRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_WhenAllFieldsAreValid()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-123",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_WhenReceiptsCategoryIsEmpty()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "",
			YnabCategoryId = "cat-123",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabCategoryMappingRequestValidator.ReceiptsCategoryMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabCategoryIdIsEmpty()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabCategoryMappingRequestValidator.YnabCategoryIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabCategoryNameIsEmpty()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-123",
			YnabCategoryName = "",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabCategoryMappingRequestValidator.YnabCategoryNameMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabCategoryGroupNameIsEmpty()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-123",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabCategoryMappingRequestValidator.YnabCategoryGroupNameMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabBudgetIdIsEmpty()
	{
		// Arrange
		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-123",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabCategoryMappingRequestValidator.YnabBudgetIdMustNotBeEmpty);
	}
}
