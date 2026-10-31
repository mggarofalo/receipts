using Common;
using Domain;
using Domain.Core;
using Infrastructure.Entities.Core;
using Riok.Mapperly.Abstractions;

namespace Infrastructure.Mapping;

[Mapper]
public partial class ReceiptItemMapper
{
	[MapProperty(nameof(ReceiptItem.UnitPrice.Amount), nameof(ReceiptItemEntity.UnitPrice))]
	[MapProperty(nameof(ReceiptItem.UnitPrice.Currency), nameof(ReceiptItemEntity.UnitPriceCurrency))]
	[MapProperty(nameof(ReceiptItem.TotalAmount.Amount), nameof(ReceiptItemEntity.TotalAmount))]
	[MapProperty(nameof(ReceiptItem.TotalAmount.Currency), nameof(ReceiptItemEntity.TotalAmountCurrency))]
	[MapperIgnoreSource(nameof(ReceiptItem.CurrentClassification))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.CurrentClassification))]
	[MapperIgnoreSource(nameof(ReceiptItem.ReceiptId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizedDescriptionId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizedDescriptionName))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.Receipt))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationOrigin))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationMappingId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationAssignmentId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationRevision))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationRevision))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReceiptId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizedDescription))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizedDescriptionId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedAt))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedByUserId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedByApiKeyId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.CascadeDeletedByParentId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationReviewState))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationReviewState))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationReviewReason))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationReviewReason))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationReviewDecisionId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationReviewDecisionId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReviewedSourceHash))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReviewedBy))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReviewedAt))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReviewMappingRevision))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationSourceHash))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationExclusionReason))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizationExclusionReason))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReviewedTargetId))]
	// Persistence derives the recorded envelope; a write supplies only an override intent.
	[MapperIgnoreSource(nameof(ReceiptItem.EnvelopeId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.EnvelopeId))]
	[MapperIgnoreSource(nameof(ReceiptItem.EnvelopeSource))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.EnvelopeSource))]
	private partial ReceiptItemEntity ToEntityPartial(ReceiptItem source);

	public ReceiptItemEntity ToEntity(ReceiptItem source)
	{
		source.ValidateForWrite();
		return ToEntityPartial(source);
	}

	[ObjectFactory]
	private static ReceiptItem FromStoredState(ReceiptItemEntity source)
		=> ReceiptItem.FromStoredState(source.Id, source.ReceiptItemCode, source.Description,
			source.Quantity, new Money(source.UnitPrice, source.UnitPriceCurrency),
			new Money(source.TotalAmount, source.TotalAmountCurrency), source.Category, source.Subcategory);

	private Money MapUnitPrice(decimal amount, Currency currency) => new(amount, currency);
	private Money MapTotalAmount(decimal amount, Currency currency) => new(amount, currency);

	[MapperIgnoreSource(nameof(ReceiptItemEntity.Receipt))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizationOrigin))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizationMappingId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizationAssignmentId))]
	[MapperIgnoreTarget(nameof(ReceiptItem.Classification))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.Classification))]
	[MapperIgnoreTarget(nameof(ReceiptItem.NormalizationIntent))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizationIntent))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.UnitPriceCurrency))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.TotalAmountCurrency))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizedDescription))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedAt))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedByUserId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedByApiKeyId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.CascadeDeletedByParentId))]
	[MapperIgnoreTarget(nameof(ReceiptItem.NormalizedDescriptionName))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.ReviewedSourceHash))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.ReviewedBy))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.ReviewedAt))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.ReviewMappingRevision))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizationSourceHash))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.ReviewedTargetId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.EnvelopeOverrideId))]
	[MapperIgnoreTarget(nameof(ReceiptItem.EnvelopeOverrideId))]
	private partial ReceiptItem ToDomainPartial(ReceiptItemEntity source);

	public ReceiptItem ToDomain(ReceiptItemEntity source)
	{
		ReceiptItem domain = ToDomainPartial(source);
		// Denormalize the neighbour's name from the nav property for read paths. Mapperly can emit
		// the direct FK via the partial method above, but the nested path through the
		// navigation requires a custom projection.
		//
		// The DISPLAY name, not the raw matched text: once a row is renamed, showing a receipt
		// item under the old receipt-scanned string would contradict every other surface
		// (RECEIPTS-876).
		domain.NormalizedDescriptionName = source.NormalizedDescription is null
			|| source.NormalizedDescription.Status == Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Rejected
			|| source.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.Excluded
			? null
			: source.NormalizedDescription.DisplayLabel ?? source.NormalizedDescription.CanonicalName;
		return domain;
	}
}
