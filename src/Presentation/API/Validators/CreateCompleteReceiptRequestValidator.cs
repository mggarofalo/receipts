using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateCompleteReceiptRequestValidator : AbstractValidator<CreateCompleteReceiptRequest>
{
	public CreateCompleteReceiptRequestValidator()
	{
		RuleFor(x => x.Receipt).SetValidator(new CreateReceiptRequestValidator());
		RuleForEach(x => x.Transactions).SetValidator(new CreateTransactionRequestValidator());
		RuleForEach(x => x.Items).SetValidator(new CreateReceiptItemRequestValidator());
		RuleForEach(x => x.Adjustments).SetValidator(new CreateAdjustmentRequestValidator());
	}
}
