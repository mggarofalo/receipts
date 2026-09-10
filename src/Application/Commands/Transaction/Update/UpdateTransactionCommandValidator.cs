using Application.Validation;
using FluentValidation;

namespace Application.Commands.Transaction.Update;

public sealed class UpdateTransactionCommandValidator : AbstractValidator<UpdateTransactionCommand>
{
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";

	public UpdateTransactionCommandValidator(AdmissionDatePolicy datePolicy)
	{
		RuleForEach(command => command.Transactions)
			.Must(transaction => datePolicy.IsNotFuture(transaction.Date))
			.WithMessage(DateCannotBeInTheFuture);
	}
}
