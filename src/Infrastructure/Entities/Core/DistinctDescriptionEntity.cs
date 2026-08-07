namespace Infrastructure.Entities.Core;

// A projection of "every description an active ReceiptItem currently uses", reconciled by
// ApplicationDbContext on save. The whole entity is one column: the description is the primary
// key, and existence in this table is the only fact it records.
//
// It carried a ProcessedAt watermark until RECEIPTS-859. That column belonged to
// ItemSimilarityEdgeRefresher, which used it to find descriptions whose similarity edges had not
// been computed yet; RECEIPTS-836 removed the refresher, after which nothing ever wrote a value —
// the reconciliation INSERT wrote a literal NULL and no code read it back.
public class DistinctDescriptionEntity
{
	public string Description { get; set; } = string.Empty;
}
