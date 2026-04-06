using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public class ResolveYnabMemoSyncRequestValidator : AbstractValidator<ResolveYnabMemoSyncRequest>
{
	public const string LocalTransactionIdMustNotBeEmpty = "Local transaction ID must not be empty.";
	public const string YnabTransactionIdMustNotBeEmpty = "YNAB transaction ID must not be empty.";
	public const string YnabTransactionIdTooLong = "YNAB transaction ID must not exceed 256 characters.";

	public ResolveYnabMemoSyncRequestValidator()
	{
		RuleFor(x => x.LocalTransactionId)
			.NotEmpty()
			.WithMessage(LocalTransactionIdMustNotBeEmpty);

		RuleFor(x => x.YnabTransactionId)
			.NotEmpty()
			.WithMessage(YnabTransactionIdMustNotBeEmpty)
			.MaximumLength(256)
			.WithMessage(YnabTransactionIdTooLong);
	}
}
