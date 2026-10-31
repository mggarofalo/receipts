using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.UndoPendingNamePromotion;

// Returns each promoted name to Pending review while it is unchanged since the promotion (RECEIPTS-1107).
public record UndoPendingNamePromotionCommand(string Actor, Guid PromotionId) : ICommand<PendingNamePromotionUndo>;
