using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class CreateYnabAccountMappingRequestValidatorTests
{
	private readonly CreateYnabAccountMappingRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.NewGuid(),
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
	public void Should_Fail_When_ReceiptsAccountIdIsEmpty()
	{
		// Arrange
		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.Empty,
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabAccountMappingRequestValidator.ReceiptsAccountIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_YnabAccountIdIsEmpty()
	{
		// Arrange
		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.NewGuid(),
			YnabAccountId = "",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabAccountMappingRequestValidator.YnabAccountIdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_YnabAccountNameIsEmpty()
	{
		// Arrange
		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.NewGuid(),
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "",
			YnabBudgetId = "budget-1",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabAccountMappingRequestValidator.YnabAccountNameMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_YnabBudgetIdIsEmpty()
	{
		// Arrange
		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.NewGuid(),
			YnabAccountId = "ynab-account-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "",
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateYnabAccountMappingRequestValidator.YnabBudgetIdMustNotBeEmpty);
	}
}
