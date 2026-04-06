using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class SyncYnabMemosBulkRequestValidator : AbstractValidator<SyncYnabMemosBulkRequest>
{
	public const string ReceiptIdsMustNotBeEmpty = "At least one receipt ID is required.";
	public const string TooManyReceiptIds = "Cannot sync more than 100 receipts at once.";
	public const string EachReceiptIdMustBeValid = "Each receipt ID must be a valid non-empty GUID.";

	public SyncYnabMemosBulkRequestValidator()
	{
		RuleFor(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(ReceiptIdsMustNotBeEmpty);

		RuleFor(x => x.ReceiptIds.Count)
			.LessThanOrEqualTo(100)
			.WithMessage(TooManyReceiptIds);

		RuleForEach(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(EachReceiptIdMustBeValid);
	}
}
