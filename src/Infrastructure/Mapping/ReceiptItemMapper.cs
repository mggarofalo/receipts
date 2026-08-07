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
	[MapperIgnoreSource(nameof(ReceiptItem.ReceiptId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizedDescriptionId))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizedDescriptionName))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.Receipt))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.ReceiptId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizedDescription))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.NormalizedDescriptionId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedAt))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedByUserId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.DeletedByApiKeyId))]
	[MapperIgnoreTarget(nameof(ReceiptItemEntity.CascadeDeletedByParentId))]
	public partial ReceiptItemEntity ToEntity(ReceiptItem source);

	private Money MapUnitPrice(decimal amount, Currency currency) => new(amount, currency);
	private Money MapTotalAmount(decimal amount, Currency currency) => new(amount, currency);

	[MapperIgnoreSource(nameof(ReceiptItemEntity.Receipt))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.UnitPriceCurrency))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.TotalAmountCurrency))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.NormalizedDescription))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedAt))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedByUserId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.DeletedByApiKeyId))]
	[MapperIgnoreSource(nameof(ReceiptItemEntity.CascadeDeletedByParentId))]
	[MapperIgnoreTarget(nameof(ReceiptItem.NormalizedDescriptionName))]
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
			? null
			: source.NormalizedDescription.DisplayLabel ?? source.NormalizedDescription.CanonicalName;
		return domain;
	}
}
