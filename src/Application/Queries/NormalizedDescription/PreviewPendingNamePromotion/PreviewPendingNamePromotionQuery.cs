using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.PreviewPendingNamePromotion;

// Read-only: the Pending review names a promotion would make Active (RECEIPTS-1107).
public record PreviewPendingNamePromotionQuery : IQuery<PendingNamePromotionPreview>;
