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
	public void Should_Pass_When_CategoryIsMissing()
	{
		// Arrange: the retired legacy label is optional and ignored.
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Test", Quantity = 1, Category = null!, Subcategory = null };

		// Act
		FluentValidation.Results.ValidationResult result = _validator.Validate(request);

		// Assert
		Assert.True(result.IsValid);
	}

	[Fact]
	public void Should_Pass_When_CategoryIsEmpty()
	{
		CreateReceiptItemRequest request = new() { UnitPrice = 9.99, Description = "Uncertain item", Quantity = 1, Category = "" };
		Assert.True(_validator.Validate(request).IsValid);
	}
}
