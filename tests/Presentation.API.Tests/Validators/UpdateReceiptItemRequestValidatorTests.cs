using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class UpdateReceiptItemRequestValidatorTests
{
	private readonly UpdateReceiptItemRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { Id = Guid.NewGuid(), UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_UnitPriceIsZero()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { UnitPrice = 0 };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.UnitPriceMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_UnitPriceIsNegative()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { UnitPrice = -5.00 };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.UnitPriceMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_IdIsEmpty()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { Id = Guid.Empty, UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.IdMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_DescriptionIsEmpty()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { Id = Guid.NewGuid(), UnitPrice = 9.99, Description = "", Quantity = 1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.DescriptionMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_QuantityIsZero()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { Id = Guid.NewGuid(), UnitPrice = 9.99, Description = "Test", Quantity = 0, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.QuantityMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_CategoryIsEmpty()
	{
		// Arrange
		UpdateReceiptItemRequest request = new() { Id = Guid.NewGuid(), UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = "" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.CategoryMustNotBeEmpty);
	}

	// RECEIPTS-655: flat-mode unit-price relaxation, parallels CreateValidator.
	[Fact]
	public void Should_Pass_When_FlatMode_UnitPriceZero_TotalPricePositive()
	{
		// Arrange
		UpdateReceiptItemRequest request = new()
		{
			Id = Guid.NewGuid(),
			UnitPrice = 0,
			TotalPrice = 4.97,
			Description = "Walmart unit-priced",
			Quantity = 1,
			Category = "Groceries",
			PricingMode = "flat"
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_FlatMode_TotalPriceMissing()
	{
		// Arrange
		UpdateReceiptItemRequest request = new()
		{
			Id = Guid.NewGuid(),
			UnitPrice = 0,
			TotalPrice = null,
			Description = "Walmart unit-priced",
			Quantity = 1,
			Category = "Groceries",
			PricingMode = "flat"
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == UpdateReceiptItemRequestValidator.TotalPriceRequiredForFlat);
	}
}
