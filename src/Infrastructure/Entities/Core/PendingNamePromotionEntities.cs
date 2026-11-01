namespace Infrastructure.Entities.Core;

/// <summary>One bulk promotion of Pending review names (RECEIPTS-1107). An operation log: not backed up.</summary>
public sealed class PendingNamePromotionEntity
{
	public Guid Id { get; set; }
	public string CreatedBy { get; set; } = "";
	public DateTimeOffset CreatedAt { get; set; }
	public int MinimumPurchases { get; set; }
	public int PurchaseCount { get; set; }
	public DateTimeOffset? UndoneAt { get; set; }
	public string? UndoneBy { get; set; }
	public int? RestoredCount { get; set; }
	public int? KeptCount { get; set; }
	public List<PendingNamePromotionEntryEntity> Entries { get; set; } = [];
}

/// <summary>
/// A promoted name and its revision just after the promotion, so undo can tell whether it has changed.
/// No foreign key to the name: the history outlives a merge or deletion.
/// </summary>
public sealed class PendingNamePromotionEntryEntity
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid PromotionId { get; set; }
	public Guid NormalizedDescriptionId { get; set; }
	public Guid RevisionAfter { get; set; }
	public bool? Restored { get; set; }
}
