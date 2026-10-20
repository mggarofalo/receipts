using Common;
using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Core;

public class ReceiptItemEntity : ISoftDeletable, IOwnedBy<ReceiptEntity>
{
	public Guid Id { get; set; }
	public Guid NormalizationRevision { get; set; }
	public string NormalizationOrigin { get; set; } = "unassigned";
	public Guid? NormalizationMappingId { get; set; }
	public Guid? NormalizationAssignmentId { get; set; }
	public Guid ReceiptId { get; set; }
	public string? ReceiptItemCode { get; set; }
	public string Description { get; set; } = string.Empty;
	public decimal Quantity { get; set; }
	public decimal UnitPrice { get; set; }
	public Currency UnitPriceCurrency { get; set; }
	public decimal TotalAmount { get; set; }
	public Currency TotalAmountCurrency { get; set; }
	public string Category { get; set; } = string.Empty;
	public string? Subcategory { get; set; }
	public Guid? NormalizedDescriptionId { get; set; }
	public double? NormalizedDescriptionMatchScore { get; set; }
	public virtual NormalizedDescriptionEntity? NormalizedDescription { get; set; }
	public virtual ReceiptEntity? Receipt { get; set; }
	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }
	public Guid? CascadeDeletedByParentId { get; set; }
}
