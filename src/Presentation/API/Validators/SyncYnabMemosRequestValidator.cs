using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class SyncYnabMemosRequestValidator : AbstractValidator<SyncYnabMemosRequest>
{
	public const string ReceiptIdMustNotBeEmpty = "Receipt ID must not be empty.";

	public SyncYnabMemosRequestValidator()
	{
		RuleFor(x => x.ReceiptId)
			.NotEmpty()
			.WithMessage(ReceiptIdMustNotBeEmpty);
	}
}
