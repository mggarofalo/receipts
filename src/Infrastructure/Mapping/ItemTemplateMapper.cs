using Common;
using Domain;
using Domain.Core;
using Infrastructure.Entities.Core;
using Riok.Mapperly.Abstractions;

namespace Infrastructure.Mapping;

[Mapper]
public partial class ItemTemplateMapper
{
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.DeletedAt))]
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.DeletedByUserId))]
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.DeletedByApiKeyId))]
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.CascadeDeletedByParentId))]
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.DefaultUnitPrice))]
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.DefaultUnitPriceCurrency))]
	// The FK carries across; the navigation does not. Materialising a NormalizedDescriptionEntity
	// from a domain model that only holds an id would invent a row EF then tries to insert.
	[MapperIgnoreTarget(nameof(ItemTemplateEntity.NormalizedDescription))]
	[MapperIgnoreSource(nameof(ItemTemplate.DefaultUnitPrice))]
	public partial ItemTemplateEntity ToEntityPartial(ItemTemplate source);

	public ItemTemplateEntity ToEntity(ItemTemplate source)
	{
		ItemTemplateEntity entity = ToEntityPartial(source);
		entity.DefaultUnitPrice = source.DefaultUnitPrice?.Amount;
		entity.DefaultUnitPriceCurrency = source.DefaultUnitPrice?.Currency;
		return entity;
	}

	[MapperIgnoreSource(nameof(ItemTemplateEntity.DeletedAt))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.DeletedByUserId))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.DeletedByApiKeyId))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.CascadeDeletedByParentId))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.DefaultUnitPrice))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.DefaultUnitPriceCurrency))]
	[MapperIgnoreSource(nameof(ItemTemplateEntity.NormalizedDescription))]
	[MapperIgnoreTarget(nameof(ItemTemplate.DefaultUnitPrice))]
	public partial ItemTemplate ToDomainPartial(ItemTemplateEntity source);

	public ItemTemplate ToDomain(ItemTemplateEntity source)
	{
		ItemTemplate domain = ToDomainPartial(source);
		if (source.DefaultUnitPrice.HasValue && source.DefaultUnitPriceCurrency.HasValue)
		{
			domain.DefaultUnitPrice = new Money(source.DefaultUnitPrice.Value, source.DefaultUnitPriceCurrency.Value);
		}
		return domain;
	}
}
