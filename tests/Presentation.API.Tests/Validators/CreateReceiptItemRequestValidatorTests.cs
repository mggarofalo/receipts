using API.Generated.Dtos;
using API.Validators;

namespace Presentation.API.Tests.Validators;

public class CreateReceiptItemRequestValidatorTests
{
	private readonly CreateReceiptItemRequestValidator _validator = new();

	[Fact]
	public void Should_Pass_When_AllFieldsValid()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_UnitPriceIsZero()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 0 };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.UnitPriceMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_UnitPriceIsNegative()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = -5.00 };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.UnitPriceMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_DescriptionIsEmpty()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "", Quantity = 1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.DescriptionMustNotBeEmpty);
	}

	[Fact]
	public void Should_Fail_When_QuantityIsZero()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Test", Quantity = 0, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.QuantityMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_QuantityIsNegative()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Test", Quantity = -1, Category = "Food" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.QuantityMustBePositive);
	}

	[Fact]
	public void Should_Fail_When_CategoryIsEmpty()
	{
		// Arrange
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = "" };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.CategoryMustNotBeEmpty);
	}

	// RECEIPTS-655: flat-priced items legitimately carry unitPrice = 0 because
	// the source receipt printed only a line total. The validator must allow
	// that as long as totalPrice is positive, and reject when totalPrice is
	// missing or zero.
	[Fact]
	public void Should_Pass_When_FlatMode_UnitPriceZero_TotalPricePositive()
	{
		// Arrange
		CreateReceiptItemRequest request = new()
		{
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
		CreateReceiptItemRequest request = new()
		{
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
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.TotalPriceRequiredForFlat);
	}

	[Fact]
	public void Should_Fail_When_FlatMode_TotalPriceZero()
	{
		// Arrange
		CreateReceiptItemRequest request = new()
		{
			UnitPrice = 0,
			TotalPrice = 0,
			Description = "Walmart unit-priced",
			Quantity = 1,
			Category = "Groceries",
			PricingMode = "flat"
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
	}

	[Fact]
	public void Should_Fail_When_QuantityMode_UnitPriceZero()
	{
		// Arrange — quantity mode keeps the legacy contract: unitPrice > 0.
		CreateReceiptItemRequest request = new()
		{
			UnitPrice = 0,
			Description = "X",
			Quantity = 1,
			Category = "Groceries",
			PricingMode = "quantity"
		};

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.ErrorMessage == CreateReceiptItemRequestValidator.UnitPriceMustBePositive);
	}
}
