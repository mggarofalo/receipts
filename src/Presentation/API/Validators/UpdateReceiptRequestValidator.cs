using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class UpdateReceiptRequestValidator : AbstractValidator<UpdateReceiptRequest>
{
	public const string IdMustNotBeEmpty = "ID must not be empty.";
	public const string LocationMustNotBeEmpty = "Location must not be empty.";
	public const string DateMustBePriorToCurrentDate = "Date must be prior to the current date";

	public UpdateReceiptRequestValidator()
	{
		RuleFor(x => x.Id)
			.NotEqual(Guid.Empty)
			.WithMessage(IdMustNotBeEmpty);

		RuleFor(x => x.Location)
			.NotEmpty()
			.WithMessage(LocationMustNotBeEmpty);

		RuleFor(x => x.Date)
			.Must(date => date.ToDateTime(TimeOnly.MinValue) <= DateTime.Today)
			.WithMessage(DateMustBePriorToCurrentDate);
	}
}
