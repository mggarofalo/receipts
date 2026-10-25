using System.ComponentModel.DataAnnotations.Schema;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Core;

public class ReceiptItemEntity : ISoftDeletable, IOwnedBy<ReceiptEntity>
{
	public Guid Id { get; set; }
	public Guid NormalizationRevision { get; set; }
	[NotMapped]
	public Domain.Taxonomy.ClassificationIntent? Classification { get; set; }
	[NotMapped]
	public ReceiptNormalizationIntent? NormalizationIntent { get; set; }
	public NormalizationReviewState NormalizationReviewState { get; set; }
	public string? NormalizationReviewReason { get; set; }
	public string? NormalizationExclusionReason { get; set; }
	public Guid? NormalizationReviewDecisionId { get; set; }
	public string? NormalizationSourceHash { get; set; }
	public Guid? ReviewedTargetId { get; set; }
	public string? ReviewedSourceHash { get; set; }
	public string? ReviewedBy { get; set; }
	public DateTimeOffset? ReviewedAt { get; set; }
	public Guid? ReviewMappingRevision { get; set; }
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
