namespace Application.Models.NormalizedDescriptions;

/// <summary>
/// The Pending review names a promotion would make Active: at least <see cref="MinimumPurchases"/>
/// live purchases, no source key shared with another name, and no other name with the same letters
/// and digits. <see cref="Fingerprint"/> identifies exactly this set.
/// </summary>
public sealed record PendingNamePromotionPreview(
	int MinimumPurchases,
	int PurchaseCount,
	string Fingerprint,
	IReadOnlyList<PromotableName> Names);

public sealed record PromotableName(Guid Id, string CanonicalName, int PurchaseCount);

/// <summary><see cref="PromotionId"/> is null when nothing was eligible, so nothing was recorded.</summary>
public sealed record PendingNamePromotionResult(Guid? PromotionId, int PromotedCount, int PurchaseCount);

public sealed record PendingNamePromotion(
	Guid Id,
	string CreatedBy,
	DateTimeOffset CreatedAt,
	int PromotedCount,
	int PurchaseCount,
	DateTimeOffset? UndoneAt,
	string? UndoneBy,
	int? RestoredCount,
	int? KeptCount);

public enum PendingNamePromotionUndoOutcome
{
	Undone,
	NotFound,
	AlreadyUndone,
}

/// <summary>
/// <see cref="KeptCount"/> names stayed Active because they changed after the promotion: renamed,
/// merged away, rejected, mapped, or given a verified purchase.
/// </summary>
public sealed record PendingNamePromotionUndo(PendingNamePromotionUndoOutcome Outcome, int RestoredCount, int KeptCount);
