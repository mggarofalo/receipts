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
	[MapProperty(nameof(ReceiptItem.TotalAmount.Amount), nameof(ReceiptItemResponse.TotalPrice))]
	[MapperIgnoreSource(nameof(ReceiptItem.PricingMode))]
	[MapperIgnoreTarget(nameof(ReceiptItemResponse.AdditionalProperties))]
	[MapperIgnoreTarget(nameof(ReceiptItemResponse.PricingMode))]
	private partial ReceiptItemResponse ToResponsePartial(ReceiptItem source);

	public ReceiptItemResponse ToResponse(ReceiptItem source)
	{
		ReceiptItemResponse response = ToResponsePartial(source);
		response.PricingMode = source.PricingMode.ToString().ToLowerInvariant();
		return response;
	}

	public ReceiptItem ToDomain(CreateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);

		PricingMode pricingMode = Enum.TryParse<PricingMode>(source.PricingMode, ignoreCase: true, out PricingMode mode)
			? mode : PricingMode.Quantity;

		Money totalAmount = ResolveTotalAmount(source.TotalPrice, quantity, unitPrice);

		return new ReceiptItem(
			Guid.Empty,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory,
			pricingMode
		);
	}

	public ReceiptItem ToDomain(UpdateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);

		PricingMode pricingMode = Enum.TryParse<PricingMode>(source.PricingMode, ignoreCase: true, out PricingMode mode)
			? mode : PricingMode.Quantity;

		Money totalAmount = ResolveTotalAmount(source.TotalPrice, quantity, unitPrice);

		return new ReceiptItem(
			source.Id,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory,
			pricingMode
		);
	}

	/// <summary>
	/// Resolve the persisted line total. When the client supplies <paramref name="totalPrice"/>
	/// (e.g. for a flat-priced item where the source receipt prints only a line total and no
	/// unit price), use that value verbatim. Otherwise compute it from quantity x unitPrice
	/// with floor-to-cent rounding to match historical behavior.
	/// </summary>
	private static Money ResolveTotalAmount(double? totalPrice, decimal quantity, decimal unitPrice)
	{
		decimal total = totalPrice.HasValue
			? (decimal)totalPrice.Value
			: Math.Floor(quantity * unitPrice * 100) / 100;
		return new Money(total, Currency.USD);
	}

	private double MapDecimalToDouble(decimal value) => (double)value;
}
