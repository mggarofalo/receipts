using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateReceiptItemRequestValidator : AbstractValidator<CreateReceiptItemRequest>
{
	public const string UnitPriceMustBePositive = "Unit price must be positive.";
	public const string UnitPriceMustBeNonNegative = "Unit price must be non-negative.";
	public const string DescriptionMustNotBeEmpty = "Description must not be empty.";
	public const string QuantityMustBePositive = "Quantity must be positive.";
	public const string CategoryMustNotBeEmpty = "Category must not be empty.";
	public const string TotalPriceRequiredForFlat = "Total price must be positive when pricing mode is 'flat'.";

	public CreateReceiptItemRequestValidator()
	{
		// Quantity-mode items need unitPrice > 0 (the legacy contract).
		// Flat-mode items legitimately carry unitPrice = 0 when the source receipt
		// prints only a line total — in that case the totalPrice carries the real value.
		RuleFor(x => x.UnitPrice)
			.GreaterThan(0)
			.When(x => !IsFlat(x.PricingMode))
			.WithMessage(UnitPriceMustBePositive);

		RuleFor(x => x.UnitPrice)
			.GreaterThanOrEqualTo(0)
			.When(x => IsFlat(x.PricingMode))
			.WithMessage(UnitPriceMustBeNonNegative);

		RuleFor(x => x.TotalPrice)
			.NotNull()
			.WithMessage(TotalPriceRequiredForFlat)
			.GreaterThan(0)
			.WithMessage(TotalPriceRequiredForFlat)
			.When(x => IsFlat(x.PricingMode));

		RuleFor(x => x.Description)
			.NotEmpty()
			.WithMessage(DescriptionMustNotBeEmpty);

		RuleFor(x => x.Quantity)
			.GreaterThan(0)
			.WithMessage(QuantityMustBePositive);

		RuleFor(x => x.Category)
			.NotEmpty()
			.WithMessage(CategoryMustNotBeEmpty);
	}

	private static bool IsFlat(string? pricingMode) =>
		string.Equals(pricingMode, "flat", StringComparison.OrdinalIgnoreCase);
}
