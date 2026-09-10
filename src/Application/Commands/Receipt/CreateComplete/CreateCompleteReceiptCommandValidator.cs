using Application.Validation;
using FluentValidation;

namespace Application.Commands.Receipt.CreateComplete;

public sealed class CreateCompleteReceiptCommandValidator : AbstractValidator<CreateCompleteReceiptCommand>
{
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";

	public CreateCompleteReceiptCommandValidator(AdmissionDatePolicy datePolicy)
	{
		RuleFor(command => command.Receipt.Date)
			.Must(datePolicy.IsNotFuture)
			.WithMessage(DateCannotBeInTheFuture);
		RuleForEach(command => command.Transactions)
			.Must(transaction => datePolicy.IsNotFuture(transaction.Date))
			.WithMessage(DateCannotBeInTheFuture);
	}
}
