using Common;
using Infrastructure.Interfaces;

namespace Infrastructure.Entities.Core;

public class ItemTemplateEntity : ISoftDeletable
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? DefaultCategory { get; set; }
	public string? DefaultSubcategory { get; set; }
	public decimal? DefaultUnitPrice { get; set; }
	public Currency? DefaultUnitPriceCurrency { get; set; }
	public string? DefaultItemCode { get; set; }
	public string? Description { get; set; }

	// RECEIPTS-881. The canonical entry this template declares; items created from the template
	// are stamped with it and skip the resolver entirely.
	public Guid? NormalizedDescriptionId { get; set; }
	public virtual NormalizedDescriptionEntity? NormalizedDescription { get; set; }

	public DateTimeOffset? DeletedAt { get; set; }
	public string? DeletedByUserId { get; set; }
	public Guid? DeletedByApiKeyId { get; set; }
	public Guid? CascadeDeletedByParentId { get; set; }
}
