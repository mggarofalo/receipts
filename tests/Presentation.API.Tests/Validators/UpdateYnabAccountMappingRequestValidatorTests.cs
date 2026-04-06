using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class UpdateYnabAccountMappingRequestValidatorTests
{
	private readonly UpdateYnabAccountMappingRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_YnabAccountIdIsEmpty()
	{
		// Arrange
		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabAccountMappingRequestValidator.YnabAccountIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_YnabAccountNameIsEmpty()
	{
		// Arrange
		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabAccountMappingRequestValidator.YnabAccountNameMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_YnabBudgetIdIsEmpty()
	{
		// Arrange
		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateYnabAccountMappingRequestValidator.YnabBudgetIdMustNotBeEmpty);
	}
}
