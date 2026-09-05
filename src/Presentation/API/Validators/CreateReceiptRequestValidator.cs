using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class CreateReceiptRequestValidator : AbstractValidator<CreateReceiptRequest>
{
	public const string DateMustBePriorToCurrentDate = "Date must be prior to the current date";

	public const string LocationMustNotBeEmpty = "Location must not be empty.";

	public CreateReceiptRequestValidator()
	{
		RuleFor(x => x.Location)
			.NotEmpty()
			.WithMessage(LocationMustNotBeEmpty);

		RuleFor(x => x.Date)
			.Must(date => date.ToDateTime(TimeOnly.MinValue) <= DateTime.Today)
			.WithMessage(DateMustBePriorToCurrentDate);
	}
}
