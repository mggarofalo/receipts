using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class UpdateYnabAccountMappingRequestValidator : AbstractValidator<UpdateYnabAccountMappingRequest>
{
	public const string YnabAccountIdMustNotBeEmpty = "YNAB account ID must not be empty.";
	public const string YnabAccountNameMustNotBeEmpty = "YNAB account name must not be empty.";
	public const string YnabBudgetIdMustNotBeEmpty = "YNAB budget ID must not be empty.";

	public UpdateYnabAccountMappingRequestValidator()
	{
		RuleFor(x => x.YnabAccountId)
			.NotEmpty()
			.WithMessage(YnabAccountIdMustNotBeEmpty);

		RuleFor(x => x.YnabAccountName)
			.NotEmpty()
			.WithMessage(YnabAccountNameMustNotBeEmpty);

		RuleFor(x => x.YnabBudgetId)
			.NotEmpty()
			.WithMessage(YnabBudgetIdMustNotBeEmpty);
	}
}
