using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class PushYnabTransactionsRequestValidator : AbstractValidator<PushYnabTransactionsRequest>
{
	public const string ReceiptIdMustNotBeEmpty = "Receipt ID must not be empty.";

	public PushYnabTransactionsRequestValidator()
	{
		RuleFor(x => x.ReceiptId)
			.NotEmpty()
			.WithMessage(ReceiptIdMustNotBeEmpty);
	}
}
