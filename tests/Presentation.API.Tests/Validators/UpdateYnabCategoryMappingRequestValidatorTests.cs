using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class UpdateYnabCategoryMappingRequestValidatorTests
{
	private readonly UpdateYnabCategoryMappingRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_WhenAllFieldsAreValid()
	{
		// Arrange
		UpdateYnabCategoryMappingRequest request = new()
		{
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
	public void Should_Fail_WhenYnabCategoryIdIsEmpty()
	{
		// Arrange
		UpdateYnabCategoryMappingRequest request = new()
		{
			YnabCategoryId = "",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabCategoryMappingRequestValidator.YnabCategoryIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabCategoryNameIsEmpty()
	{
		// Arrange
		UpdateYnabCategoryMappingRequest request = new()
		{
			YnabCategoryId = "cat-123",
			YnabCategoryName = "",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabCategoryMappingRequestValidator.YnabCategoryNameMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_WhenYnabBudgetIdIsEmpty()
	{
		// Arrange
		UpdateYnabCategoryMappingRequest request = new()
		{
			YnabCategoryId = "cat-123",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Immediate Obligations",
			YnabBudgetId = "",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabCategoryMappingRequestValidator.YnabBudgetIdMustNotBeEmpty);
	}
}
