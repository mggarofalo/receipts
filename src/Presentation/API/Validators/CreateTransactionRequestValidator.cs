using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateTransactionRequestValidator : AbstractValidator<CreateTransactionRequest>
{
	public const string AmountMustBeNonZero = "Amount must be non-zero.";
	public const string DateMustBePriorToCurrentDate = "Date must be prior to the current date";

	public const string CardIdMustNotBeEmpty = "Card ID must not be empty.";

	public CreateTransactionRequestValidator()
	{
		RuleFor(x => x.Amount)
			.NotEqual(0)
			.WithMessage(AmountMustBeNonZero);

		RuleFor(x => x.Date)
			.Must(date => date.ToDateTime(TimeOnly.MinValue) <= DateTime.Today)
			.WithMessage(DateMustBePriorToCurrentDate);

		RuleFor(x => x.CardId)
			.NotEqual(Guid.Empty)
			.WithMessage(CardIdMustNotBeEmpty);
	}
}
