using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class SelectYnabBudgetRequestValidatorTests
{
	private readonly SelectYnabBudgetRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_BudgetIdIsValidUuid()
	{
		// Arrange
		SelectYnabBudgetRequest request = new() { BudgetId = Guid.NewGuid().ToString() };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_BudgetIdIsEmpty()
	{
		// Arrange
		SelectYnabBudgetRequest request = new() { BudgetId = "" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == SelectYnabBudgetRequestValidator.BudgetIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_BudgetIdIsNotValidUuid()
	{
		// Arrange
		SelectYnabBudgetRequest request = new() { BudgetId = "not-a-uuid" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == SelectYnabBudgetRequestValidator.BudgetIdMustBeValidUuid);
	}
}
