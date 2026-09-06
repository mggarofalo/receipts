using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateItemTemplateRequestValidator : AbstractValidator<CreateItemTemplateRequest>
{
	public const string NameMustNotBeEmpty = "Name must not be empty.";
	public const string DefaultUnitPriceMustBePositive = "Default unit price must be positive.";

	public CreateItemTemplateRequestValidator()
	{
		RuleFor(x => x.Name)
			.NotEmpty()
			.WithMessage(NameMustNotBeEmpty);

		RuleFor(x => x.DefaultUnitPrice)
			.GreaterThan(0)
			.WithMessage(DefaultUnitPriceMustBePositive)
			.Must(ItemTemplatePriceValidation.FitsColumn)
			.WithMessage(ItemTemplatePriceValidation.OutOfRange)
			.When(x => x.DefaultUnitPrice.HasValue);
	}
}
