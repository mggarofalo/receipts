using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class UpdateTransactionRequestValidatorTests
{
	private readonly UpdateTransactionRequestValidator _validator = new(ValidatorTestClock.Policy);

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		UpdateTransactionRequest request = new()
		{
			Id = Guid.NewGuid(),
			Amount = 100,
			Date = ValidatorTestClock.Today,
			CardId = Guid.NewGuid()
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_IdIsEmpty()
	{
		// Arrange
		UpdateTransactionRequest request = new()
		{
			Id = Guid.Empty,
			Amount = 100,
			Date = ValidatorTestClock.Today,
			CardId = Guid.NewGuid()
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateTransactionRequestValidator.IdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_AmountIsZero()
	{
		// Arrange
		UpdateTransactionRequest request = new()
		{
			Id = Guid.NewGuid(),
			Amount = 0,
			Date = ValidatorTestClock.Today,
			CardId = Guid.NewGuid()
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateTransactionRequestValidator.AmountMustBeNonZero);
	}

	[Fact]
	public void Should_Fail_When_DateIsInTheFuture()
	{
		// Arrange
		UpdateTransactionRequest request = new()
		{
			Id = Guid.NewGuid(),
			Amount = 100,
			Date = ValidatorTestClock.Today.AddDays(1),
			CardId = Guid.NewGuid()
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateTransactionRequestValidator.DateMustBePriorToCurrentDate);
	}

	[Fact]
	public void Should_Fail_When_CardIdIsEmpty()
	{
		// Arrange
		UpdateTransactionRequest request = new()
		{
			Id = Guid.NewGuid(),
			Amount = 100,
			Date = ValidatorTestClock.Today,
			CardId = Guid.Empty
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateTransactionRequestValidator.CardIdMustNotBeEmpty);
	}
}
