using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.Split;

/// <summary>
/// Detaches one or more ReceiptItems from their current NormalizedDescription into a single new
/// canonical entry, and re-points them at it. Used to unpick bad auto-merges or isolate items
/// that were auto-classified into the wrong canonical group.
/// </summary>
/// <remarks>
/// The name is supplied by the caller rather than derived from the selection (RECEIPTS-877).
/// Splitting is a deliberate correction and the person doing it knows what the group should be
/// called; deriving it also sidesteps the heterogeneous-selection problem entirely — there is no
/// sensible automatic answer when the selected items read "MILK 2%", "milk gal" and "WHOLE MILK".
///
/// The service throws KeyNotFoundException if any ReceiptItem does not exist.
/// </remarks>
public record SplitNormalizedDescriptionCommand(
	IReadOnlyList<Guid> ReceiptItemIds,
	string CanonicalName) : ICommand<NormalizedDescriptionDetail>;
