using API.Generated.Dtos;
using Common;
using Domain;
using Domain.Core;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class ItemTemplateMapper
{
	[MapperIgnoreTarget(nameof(ItemTemplateResponse.AdditionalProperties))]
	[MapperIgnoreSource(nameof(ItemTemplate.DefaultUnitPrice))]
	[MapperIgnoreTarget(nameof(ItemTemplateResponse.DefaultUnitPrice))]
	[MapperIgnoreTarget(nameof(ItemTemplateResponse.DefaultUnitPriceCurrency))]
	// The canonical link is deliberately not on the wire yet (RECEIPTS-881). No client reads it:
	// items are stamped server-side from the template id they post, so the browser never needs to
	// know which canonical row a template declares. RECEIPTS-930 adds it if the review-queue
	// "link to template" UI turns out to need it — until then it is an internal detail, and an
	// exposed field is a contract we would have to keep.
	[MapperIgnoreSource(nameof(ItemTemplate.NormalizedDescriptionId))]
	public partial ItemTemplateResponse ToResponsePartial(ItemTemplate source);

	public ItemTemplateResponse ToResponse(ItemTemplate source)
	{
		ItemTemplateResponse response = ToResponsePartial(source);
		if (source.DefaultUnitPrice != null)
		{
			response.DefaultUnitPrice = (double)source.DefaultUnitPrice.Amount;
			response.DefaultUnitPriceCurrency = source.DefaultUnitPrice.Currency.ToString();
		}
		return response;
	}

	public ItemTemplate ToDomain(CreateItemTemplateRequest source)
	{
		Money? defaultUnitPrice = source.DefaultUnitPrice.HasValue
			? new Money((decimal)source.DefaultUnitPrice.Value, Currency.USD)
			: null;

		return new ItemTemplate(
			Guid.Empty,
			source.Name,
			source.DefaultCategory,
			source.DefaultSubcategory,
			defaultUnitPrice,
			source.DefaultItemCode,
			source.Description
		);
	}

	public ItemTemplate ToDomain(UpdateItemTemplateRequest source)
	{
		Money? defaultUnitPrice = source.DefaultUnitPrice.HasValue
			? new Money((decimal)source.DefaultUnitPrice.Value, Currency.USD)
			: null;

		return new ItemTemplate(
			source.Id,
			source.Name,
			source.DefaultCategory,
			source.DefaultSubcategory,
			defaultUnitPrice,
			source.DefaultItemCode,
			source.Description
		);
	}
}
