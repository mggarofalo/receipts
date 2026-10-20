using Common;
using Domain;
using Domain.Core;
using Infrastructure.Entities.Core;
using Riok.Mapperly.Abstractions;

namespace Infrastructure.Mapping;

[Mapper]
public partial class ReceiptMapper
{
	[MapProperty(nameof(Receipt.TaxAmount.Amount), nameof(ReceiptEntity.TaxAmount))]
	[MapProperty(nameof(Receipt.TaxAmount.Currency), nameof(ReceiptEntity.TaxAmountCurrency))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.NormalizationRevision))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.DeletedAt))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.DeletedByUserId))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.DeletedByApiKeyId))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.CascadeDeletedByParentId))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.OriginalImagePath))]
	[MapperIgnoreTarget(nameof(ReceiptEntity.ProcessedImagePath))]
	public partial ReceiptEntity ToEntity(Receipt source);

	private Money MapTaxAmount(decimal amount, Currency currency) => new(amount, currency);

	[MapperIgnoreSource(nameof(ReceiptEntity.TaxAmountCurrency))]
	[MapperIgnoreSource(nameof(ReceiptEntity.NormalizationRevision))]
	[MapperIgnoreSource(nameof(ReceiptEntity.DeletedAt))]
	[MapperIgnoreSource(nameof(ReceiptEntity.DeletedByUserId))]
	[MapperIgnoreSource(nameof(ReceiptEntity.DeletedByApiKeyId))]
	[MapperIgnoreSource(nameof(ReceiptEntity.CascadeDeletedByParentId))]
	[MapperIgnoreSource(nameof(ReceiptEntity.OriginalImagePath))]
	[MapperIgnoreSource(nameof(ReceiptEntity.ProcessedImagePath))]
	public partial Receipt ToDomain(ReceiptEntity source);
}
