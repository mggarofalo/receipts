using Application.Validation;
using FluentValidation;

namespace Application.Commands.Receipt.Create;

public sealed class CreateReceiptCommandValidator : AbstractValidator<CreateReceiptCommand>
{
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";

	public CreateReceiptCommandValidator(AdmissionDatePolicy datePolicy)
	{
		RuleForEach(command => command.Receipts)
			.Must(receipt => datePolicy.IsNotFuture(receipt.Date))
			.WithMessage(DateCannotBeInTheFuture);
	}
}
