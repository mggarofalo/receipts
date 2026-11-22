using Application.Interfaces;
using Application.Interfaces.Services;

namespace Application.Commands.ReceiptItem.EnvelopeAutomatic;

/// <summary>A person's Automatic: re-derives an item's envelope once from the receipt's paid-from, else its item type's default.</summary>
public record SetReceiptItemEnvelopeAutomaticCommand(Guid ItemId) : ICommand<ItemEnvelopeAutomaticResult>;
