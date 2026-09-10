using Application.Validation;
using FluentValidation;

namespace Application.Commands.Transaction.Create;

public sealed class CreateTransactionCommandValidator : AbstractValidator<CreateTransactionCommand>
{
	public const string DateCannotBeInTheFuture = "Date cannot be in the future";

	public CreateTransactionCommandValidator(AdmissionDatePolicy datePolicy)
	{
		RuleForEach(command => command.Transactions)
			.Must(transaction => datePolicy.IsNotFuture(transaction.Date))
			.WithMessage(DateCannotBeInTheFuture);
	}
}
