using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class SelectYnabBudgetRequestValidator : AbstractValidator<SelectYnabBudgetRequest>
{
	public const string BudgetIdMustNotBeEmpty = "Budget ID must not be empty.";

	public SelectYnabBudgetRequestValidator()
	{
		RuleFor(x => x.BudgetId)
			.NotEmpty()
			.WithMessage(BudgetIdMustNotBeEmpty);
	}
}
