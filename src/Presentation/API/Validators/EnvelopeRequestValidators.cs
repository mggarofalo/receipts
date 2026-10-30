using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public static class EnvelopeRequestRules
{
	public const string NameMustNotBeBlank = "Name must not be blank.";
}

public class CreateEnvelopeRequestValidator : AbstractValidator<CreateEnvelopeRequest>
{
	public CreateEnvelopeRequestValidator()
	{
		RuleFor(x => x.Name).Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(EnvelopeRequestRules.NameMustNotBeBlank);
	}
}

public class UpdateEnvelopeRequestValidator : AbstractValidator<UpdateEnvelopeRequest>
{
	public UpdateEnvelopeRequestValidator()
	{
		RuleFor(x => x.Name).Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(EnvelopeRequestRules.NameMustNotBeBlank);
	}
}

public class CreateOccasionRequestValidator : AbstractValidator<CreateOccasionRequest>
{
	public CreateOccasionRequestValidator()
	{
		RuleFor(x => x.Name).Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(EnvelopeRequestRules.NameMustNotBeBlank);
	}
}

public class UpdateOccasionRequestValidator : AbstractValidator<UpdateOccasionRequest>
{
	public UpdateOccasionRequestValidator()
	{
		RuleFor(x => x.Name).Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(EnvelopeRequestRules.NameMustNotBeBlank);
	}
}

public class SetYnabEnvelopeMappingRequestValidator : AbstractValidator<SetYnabEnvelopeMappingRequest>
{
	public const string YnabCategoryIdMustNotBeEmpty = "YNAB category ID must not be empty.";

	public SetYnabEnvelopeMappingRequestValidator()
	{
		RuleFor(x => x.YnabCategoryId).Must(id => !string.IsNullOrWhiteSpace(id)).WithMessage(YnabCategoryIdMustNotBeEmpty);
	}
}
