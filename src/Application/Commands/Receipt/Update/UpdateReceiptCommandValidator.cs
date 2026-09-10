using Application.Validation;
using FluentValidation;

namespace Application.Commands.Receipt.Update;

public sealed class UpdateReceiptCommandValidator : AbstractValidator<UpdateReceiptCommand>
{
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";

	public UpdateReceiptCommandValidator(AdmissionDatePolicy datePolicy)
	{
		RuleForEach(command => command.Receipts)
			.Must(receipt => datePolicy.IsNotFuture(receipt.Date))
			.WithMessage(DateCannotBeInTheFuture);
	}
}
