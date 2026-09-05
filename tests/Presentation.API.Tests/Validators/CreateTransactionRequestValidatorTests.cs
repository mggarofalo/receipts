using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class CreateTransactionRequestValidatorTests
{
	private readonly CreateTransactionRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_ValidTransaction()
	{
		// Arrange
		CreateTransactionRequest transaction = new()
		{
			Amount = 100,
			Date = DateOnly.FromDateTime(DateTime.Today),
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
			Date = DateOnly.FromDateTime(DateTime.Today),
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
		DateOnly pastDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
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
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
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
			Date = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
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
			Date = DateOnly.FromDateTime(DateTime.Today),
			CardId = Guid.Empty,
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(transaction);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateTransactionRequestValidator.CardIdMustNotBeEmpty);
	}
}
