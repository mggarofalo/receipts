using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class BulkPushYnabTransactionsRequestValidator : AbstractValidator<BulkPushYnabTransactionsRequest>
{
	public const string ReceiptIdsMustNotBeEmpty = "At least one receipt ID is required.";
	public const string ReceiptIdMustNotBeEmpty = "Each receipt ID must not be empty.";

	public BulkPushYnabTransactionsRequestValidator()
	{
		RuleFor(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(ReceiptIdsMustNotBeEmpty);

		RuleForEach(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(ReceiptIdMustNotBeEmpty);
	}
}
