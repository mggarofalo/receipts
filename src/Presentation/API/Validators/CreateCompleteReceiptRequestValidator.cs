using API.Generated.Dtos;
using Application.Validation;
using FluentValidation;

namespace API.Validators;

public class CreateCompleteReceiptRequestValidator : AbstractValidator<CreateCompleteReceiptRequest>
{
	public CreateCompleteReceiptRequestValidator(AdmissionDatePolicy datePolicy)
	{
		RuleFor(x => x.Receipt).SetValidator(new CreateReceiptRequestValidator(datePolicy));
		RuleForEach(x => x.Transactions).SetValidator(new CreateTransactionRequestValidator(datePolicy));
		RuleForEach(x => x.Items).SetValidator(new CreateReceiptItemRequestValidator());
		RuleForEach(x => x.Adjustments).SetValidator(new CreateAdjustmentRequestValidator());
	}
}
