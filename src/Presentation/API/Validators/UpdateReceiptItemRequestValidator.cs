using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class UpdateReceiptItemRequestValidator : AbstractValidator<UpdateReceiptItemRequest>
{
	public const string UnitPriceMustBePositive = "Unit price must be positive.";
	public const string UnitPriceMustBeNonNegative = "Unit price must be non-negative.";
	public const string IdMustNotBeEmpty = "ID must not be empty.";
	public const string DescriptionMustNotBeEmpty = "Description must not be empty.";
	public const string QuantityMustBePositive = "Quantity must be positive.";
	public const string CategoryMustNotBeEmpty = "Category must not be empty.";
	public const string TotalPriceRequiredForFlat = "Total price must be positive when pricing mode is 'flat'.";

	public UpdateReceiptItemRequestValidator()
	{
		RuleFor(x => x.Id)
			.NotEqual(Guid.Empty)
			.WithMessage(IdMustNotBeEmpty);

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
