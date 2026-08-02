using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

/// <summary>
/// Guards the accept / un-accept duplicate-group endpoints (RECEIPTS-834).
///
/// The upper bound is the load-bearing rule. Accepting a group expands to C(n,2) pairs, so an
/// unbounded list is quadratic work in one transaction. This is reachable without an attacker: in
/// dateAndLocation + normalized mode a "group" is every receipt sharing a (date, location), so one
/// sloppy bulk import can put hundreds of receipts behind a single click. 100 matches
/// <see cref="BulkPushYnabTransactionsRequestValidator.MaxReceiptIds"/>, which caps the identical
/// receiptIds shape.
/// </summary>
public class AcceptDuplicateGroupRequestValidator : AbstractValidator<AcceptDuplicateGroupRequest>
{
	public const int MaxReceiptIds = 100;
	public const int MinReceiptIds = 2;
	public const string ReceiptIdsTooFew = "At least 2 distinct receipt IDs are required.";
	public const string ReceiptIdsTooMany = "Cannot accept more than 100 receipts at once.";
	public const string ReceiptIdMustNotBeEmpty = "Each receipt ID must not be empty.";

	public AcceptDuplicateGroupRequestValidator()
	{
		// Distinct, because a group is a set: [x, x] is one receipt sent twice, not a pair.
		RuleFor(x => x.ReceiptIds)
			.Must(ids => ids is not null && ids.Distinct().Count() >= MinReceiptIds)
			.WithMessage(ReceiptIdsTooFew);

		RuleFor(x => x.ReceiptIds)
			.Must(ids => ids is null || ids.Count <= MaxReceiptIds)
			.WithMessage(ReceiptIdsTooMany);

		RuleForEach(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(ReceiptIdMustNotBeEmpty);
	}
}
