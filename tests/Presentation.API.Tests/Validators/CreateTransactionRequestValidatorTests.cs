using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class CreateTransactionRequestValidatorTests
{
	private readonly CreateTransactionRequestValidator _validator = new(ValidatorTestClock.Policy);

	[Fact]
	public void Should_Pass_When_ValidTransaction()
	{
		// Arrange
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = ValidatorTestClock.Today,
			CardId = Guid.NewGuid(),
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_AmountIsZero()
	{
		// Arrange
		CreateTransactionRequest transaction = new()
		{
			Amount = 0,
			Date = ValidatorTestClock.Today,
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateTransactionRequestValidator.AmountMustBeNonZero);
	}

	[Fact]
	public void Should_Pass_When_DateIsInThePast()
	{
		// Arrange
		DateOnly pastDate = ValidatorTestClock.Today.AddDays(-1);
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = pastDate,
			CardId = Guid.NewGuid(),
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Pass_When_DateIsToday()
	{
		// Arrange
		DateOnly today = ValidatorTestClock.Today;
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = today,
			CardId = Guid.NewGuid(),
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_DateIsInTheFuture()
	{
		// Arrange
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = ValidatorTestClock.Today.AddDays(1),
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateTransactionRequestValidator.DateMustBePriorToCurrentDate);
	}

	[Fact]
	public void Should_Fail_When_CardIdIsEmpty()
	{
		// Arrange
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = ValidatorTestClock.Today,
			CardId = Guid.Empty,
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateTransactionRequestValidator.CardIdMustNotBeEmpty);
	}
}
