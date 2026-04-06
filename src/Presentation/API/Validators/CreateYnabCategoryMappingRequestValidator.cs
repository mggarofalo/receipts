using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateYnabCategoryMappingRequestValidator : AbstractValidator<CreateYnabCategoryMappingRequest>
{
	public const string ReceiptsCategoryMustNotBeEmpty = "Receipts category must not be empty.";
	public const string YnabCategoryIdMustNotBeEmpty = "YNAB category ID must not be empty.";
	public const string YnabCategoryNameMustNotBeEmpty = "YNAB category name must not be empty.";
	public const string YnabCategoryGroupNameMustNotBeEmpty = "YNAB category group name must not be empty.";
	public const string YnabBudgetIdMustNotBeEmpty = "YNAB budget ID must not be empty.";

	public CreateYnabCategoryMappingRequestValidator()
	{
		RuleFor(x => x.ReceiptsCategory)
			.NotEmpty()
			.WithMessage(ReceiptsCategoryMustNotBeEmpty);

		RuleFor(x => x.YnabCategoryId)
			.NotEmpty()
			.WithMessage(YnabCategoryIdMustNotBeEmpty);

		RuleFor(x => x.YnabCategoryName)
			.NotEmpty()
			.WithMessage(YnabCategoryNameMustNotBeEmpty);

		RuleFor(x => x.YnabCategoryGroupName)
			.NotEmpty()
			.WithMessage(YnabCategoryGroupNameMustNotBeEmpty);

		RuleFor(x => x.YnabBudgetId)
			.NotEmpty()
			.WithMessage(YnabBudgetIdMustNotBeEmpty);
	}
}
