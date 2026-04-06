using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class SelectYnabBudgetRequestValidator : AbstractValidator<SelectYnabBudgetRequest>
{
	public const string BudgetIdMustNotBeEmpty = "Budget ID must not be empty.";
	public const string BudgetIdMustBeValidUuid = "Budget ID must be a valid UUID.";

	public SelectYnabBudgetRequestValidator()
	{
		RuleFor(x => x.BudgetId)
			.NotEmpty()
			.WithMessage(BudgetIdMustNotBeEmpty)
			.Must(id => Guid.TryParse(id, out _))
			.WithMessage(BudgetIdMustBeValidUuid);
	}
}
