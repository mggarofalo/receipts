using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

/// <summary>
/// Guards the un-accept endpoint (RECEIPTS-834).
///
/// Deliberately has NO upper bound on the ID count, unlike
/// <see cref="AcceptDuplicateGroupRequestValidator"/>. That cap exists because accepting expands to
/// C(n,2) INSERTs, so the work is quadratic in the input. Un-accepting is a single DELETE whose
/// predicate is linear in the input and whose effect is bounded by the rows that already exist —
/// there is no amplification, so the quadratic rationale does not transfer.
///
/// Sharing one request contract between the two endpoints was an outright bug. Accepted groups are
/// connected components of the pair graph, and components merge whenever an acceptance bridges two
/// of them, so a component can exceed 100 members without any single accept call doing so: accept
/// two 51-receipt clusters under matchOn=dateAndLocation, then accept one straddling 2-receipt
/// cluster under matchOn=dateAndTotal, and the component is 102. Undo posts all 102 member IDs and
/// the shared cap rejected it, leaving the group permanently un-undoable — and, if one of its
/// members was soft-deleted, its pairs unreachable by any client-producible set at all.
/// </summary>
public class UnacceptDuplicateGroupRequestValidator : AbstractValidator<UnacceptDuplicateGroupRequest>
{
	public const int MinReceiptIds = 2;
	public const string ReceiptIdsTooFew = "At least 2 distinct receipt IDs are required.";
	public const string ReceiptIdMustNotBeEmpty = "Each receipt ID must not be empty.";

	public UnacceptDuplicateGroupRequestValidator()
	{
		// Distinct, because a group is a set: [x, x] is one receipt sent twice, not a pair.
		RuleFor(x => x.ReceiptIds)
			.Must(ids => ids is not null && ids.Distinct().Count() >= MinReceiptIds)
			.WithMessage(ReceiptIdsTooFew);

		RuleForEach(x => x.ReceiptIds)
			.NotEmpty()
			.WithMessage(ReceiptIdMustNotBeEmpty);
	}
}
