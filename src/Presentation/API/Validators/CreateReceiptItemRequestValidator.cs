using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateReceiptItemRequestValidator : AbstractValidator<CreateReceiptItemRequest>
{
	public const string UnitPriceMustBePositive = "Unit price must be positive.";
	public const string DescriptionMustNotBeEmpty = "Description must not be empty.";
	public const string QuantityMustBePositive = "Quantity must be positive.";

	public CreateReceiptItemRequestValidator()
	{
		RuleFor(x => x.UnitPrice)
			.GreaterThan(0)
			.WithMessage(UnitPriceMustBePositive);

		RuleFor(x => x.Description)
			.NotEmpty()
			.WithMessage(DescriptionMustNotBeEmpty);

		RuleFor(x => x.Quantity)
			.GreaterThan(0)
			.WithMessage(QuantityMustBePositive);
	}
}
