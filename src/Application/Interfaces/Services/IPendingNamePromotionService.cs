using Application.Models.NormalizedDescriptions;

namespace Application.Interfaces.Services;

/// <summary>
/// Bulk promotion of consistent Pending review names to Active, previewed and undoable
/// (RECEIPTS-1107, docs/decisions/2026-10-31-promote-consistent-pending-names.md).
/// </summary>
public interface IPendingNamePromotionService
{
	Task<PendingNamePromotionPreview> PreviewAsync(CancellationToken cancellationToken);

	/// <summary>Null when the eligible set no longer matches <paramref name="expectedFingerprint"/>.</summary>
	Task<PendingNamePromotionResult?> PromoteAsync(string actor, string expectedFingerprint, CancellationToken cancellationToken);

	Task<IReadOnlyList<PendingNamePromotion>> ListAsync(CancellationToken cancellationToken);

	Task<PendingNamePromotionUndo> UndoAsync(string actor, Guid promotionId, CancellationToken cancellationToken);
}
