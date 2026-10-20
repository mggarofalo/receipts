using API.Generated.Dtos;
using Common;
using Domain;
using Domain.Core;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class ReceiptItemMapper
{
	[MapProperty(nameof(ReceiptItem.UnitPrice.Amount), nameof(ReceiptItemResponse.UnitPrice))]
	[MapperIgnoreSource(nameof(ReceiptItem.TotalAmount))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationIntent))]
	[MapperIgnoreTarget(nameof(ReceiptItemResponse.AdditionalProperties))]
	public partial ReceiptItemResponse ToResponse(ReceiptItem source);

	public ReceiptItem ToDomain(CreateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);
		Money totalAmount = new(Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero), Currency.USD);

		return new ReceiptItem(
			Guid.Empty,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory
		)
		{ NormalizationIntent = MapIntent(source.NormalizationIntent) };
	}

	public ReceiptItem ToDomain(UpdateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);
		Money totalAmount = new(Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero), Currency.USD);

		return new ReceiptItem(
			source.Id,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory
		)
		{ NormalizationIntent = MapIntent(source.NormalizationIntent) };
	}

	private static Domain.NormalizedDescriptions.ReceiptNormalizationIntent? MapIntent(API.Generated.Dtos.ReceiptNormalizationIntent? value)
		=> value is null ? null : new((Domain.NormalizedDescriptions.ReceiptNormalizationIntentKind)value.Kind,
			value.ClientRowId, value.SourceFingerprint, value.TargetId, value.ExpectedTargetRevision, value.ExpectedMappingRevision,
			value.ExpectedItemRevision, value.NewName, value.RememberSource, value.Reason);

	private double MapDecimalToDouble(decimal value) => (double)value;
}
