using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class UpdateItemTemplateRequestValidator : AbstractValidator<UpdateItemTemplateRequest>
{
	public const string IdMustNotBeEmpty = "ID must not be empty.";
	public const string NameMustNotBeEmpty = "Name must not be empty.";
	public const string DefaultUnitPriceMustBePositive = "Default unit price must be positive.";

	public UpdateItemTemplateRequestValidator()
	{
		RuleFor(x => x.Id)
			.NotEqual(Guid.Empty)
			.WithMessage(IdMustNotBeEmpty);

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
