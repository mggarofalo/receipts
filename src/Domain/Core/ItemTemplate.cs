namespace Domain.Core;

public class ItemTemplate
{
	public Guid Id { get; set; }
	public string Name { get; set; }
	public string? DefaultCategory { get; set; }
	public string? DefaultSubcategory { get; set; }
	public Money? DefaultUnitPrice { get; set; }
	public string? DefaultItemCode { get; set; }
	public string? Description { get; set; }

	// The canonical entry this template declares (RECEIPTS-881). Templates and normalized
	// descriptions were two independent vocabularies that never referenced each other, so an item
	// entered *from a template* still went through the ANN resolver as unknown text and could land
	// in the review queue — asking a human to confirm a grouping the same human had already
	// declared by creating the template.
	//
	// Set by the service, not the constructor: it is resolved against the registry rather than
	// supplied by a caller, exactly like ReceiptItem.NormalizedDescriptionId. Nullable because a
	// template created before this existed has not been linked yet, and links lazily on next use.
	public Guid? NormalizedDescriptionId { get; set; }

	public const string NameCannotBeEmpty = "Name cannot be empty";

	public ItemTemplate(Guid id, string name, string? defaultCategory = null, string? defaultSubcategory = null, Money? defaultUnitPrice = null, string? defaultItemCode = null, string? description = null)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException(NameCannotBeEmpty, nameof(name));
		}

		Id = id;
		Name = name;
		DefaultCategory = defaultCategory;
		DefaultSubcategory = defaultSubcategory;
		DefaultUnitPrice = defaultUnitPrice;
		DefaultItemCode = defaultItemCode;
		Description = description;
	}
}
