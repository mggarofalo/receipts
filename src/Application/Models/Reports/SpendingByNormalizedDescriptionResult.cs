using Domain.NormalizedDescriptions;

namespace Application.Models.Reports;

/// <summary>
/// One spending bucket. <paramref name="Status"/> is what makes the review queue a real gate
/// (RECEIPTS-875): a PendingReview bucket is money the resolver grouped on its own authority and
/// nobody has confirmed, so the client renders it as provisional. It stays in the totals — dropping
/// it would stop the report reconciling against receipt totals — it just stops looking settled.
/// Null on the synthetic "(Not Normalized)" bucket, which has no backing row to carry a status.
/// </summary>
public record SpendingByNormalizedDescriptionItem(
	string CanonicalName,
	decimal TotalAmount,
	string Currency,
	int ItemCount,
	DateTimeOffset? FirstSeen,
	DateTimeOffset? LastSeen,
	NormalizedDescriptionStatus? Status);

public record SpendingByNormalizedDescriptionResult(
	List<SpendingByNormalizedDescriptionItem> Items,
	int TotalCount,
	decimal GrandTotal,
	DateTimeOffset? FromDate,
	DateTimeOffset? ToDate);
