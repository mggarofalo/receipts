using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.Split;

// Detaches a single ReceiptItem from its current NormalizedDescription by creating a new
// canonical entry for the item's raw description and re-pointing the item at it. Used by
// admins to unpick bad merges or isolate an item that was auto-classified into the wrong
// canonical group. The service throws KeyNotFoundException if the ReceiptItem does not exist.
public record SplitNormalizedDescriptionCommand(Guid ReceiptItemId) : ICommand<NormalizedDescriptionDetail>;
