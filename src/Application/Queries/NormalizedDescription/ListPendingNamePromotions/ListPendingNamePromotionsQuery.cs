using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.ListPendingNamePromotions;

// The most recent promotions, newest first, with their undo state (RECEIPTS-1107).
public record ListPendingNamePromotionsQuery : IQuery<IReadOnlyList<PendingNamePromotion>>;
