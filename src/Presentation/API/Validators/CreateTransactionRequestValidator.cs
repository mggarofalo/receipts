using API.Generated.Dtos;
using Application.Validation;
using FluentValidation;

namespace API.Validators;

public class CreateTransactionRequestValidator : AbstractValidator<CreateTransactionRequest>
{
	public const string AmountMustBeNonZero = "Amount must be non-zero.";
	public const string DateMustBePriorToCurrentDate = "Date must be prior to the current date";

	public const string CardIdMustNotBeEmpty = "Card ID must not be empty.";

	public CreateTransactionRequestValidator(AdmissionDatePolicy datePolicy)
	{
		RuleFor(x => x.Amount)
			.NotEqual(0)
			.WithMessage(AmountMustBeNonZero);

		RuleFor(x => x.Date)
			.Must(datePolicy.IsNotFuture)
			.WithMessage(DateMustBePriorToCurrentDate);

		RuleFor(x => x.CardId)
			.NotEqual(Guid.Empty)
			.WithMessage(CardIdMustNotBeEmpty);
	}
}
