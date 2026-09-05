using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class UpdateTransactionRequestValidator : AbstractValidator<UpdateTransactionRequest>
{
	public const string IdMustNotBeEmpty = "ID must not be empty.";
	public const string AmountMustBeNonZero = "Amount must be non-zero.";
	public const string DateMustBePriorToCurrentDate = "Date must be prior to the current date";
	public const string CardIdMustNotBeEmpty = "Card ID must not be empty.";

	public UpdateTransactionRequestValidator()
	{
		RuleFor(x => x.Id)
			.NotEqual(Guid.Empty)
			.WithMessage(IdMustNotBeEmpty);

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
