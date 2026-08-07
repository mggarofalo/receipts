using Common;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class ItemTemplateEntityConfiguration : IEntityTypeConfiguration<ItemTemplateEntity>
{
	public void Configure(EntityTypeBuilder<ItemTemplateEntity> builder)
	{
		builder.ToTable("ItemTemplates", "library", t => t.HasCheckConstraint(
			"CK_ItemTemplates_Money_Consistency",
			"((\"DefaultUnitPrice\" IS NULL AND \"DefaultUnitPriceCurrency\" IS NULL) OR (\"DefaultUnitPrice\" IS NOT NULL AND \"DefaultUnitPriceCurrency\" IS NOT NULL))"));

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.HasIndex(e => e.Name)
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		// RECEIPTS-881. SetNull mirrors ReceiptItem's FK to the same table: a canonical entry can
		// legitimately disappear (a merge deletes the discarded row), and blocking that to protect
		// a template's convenience link would be the wrong trade.
		//
		// SetNull is a backstop, not the mechanism. MergeAsync re-points templates explicitly for
		// the same reason it re-points receipt items — relying on the database to null the column
		// would silently unlink every template pointing at the merged-away row, with nothing
		// raised anywhere. Same class of bug as the soft-deleted-transaction stranding in
		// RECEIPTS-801.
		builder.HasOne(e => e.NormalizedDescription)
			.WithMany()
			.HasForeignKey(e => e.NormalizedDescriptionId)
			.OnDelete(DeleteBehavior.SetNull);

		// Deliberately NOT AutoInclude, unlike ReceiptItem's. Nothing that reads templates needs
		// the canonical row, and an auto-include would drag a 1024-float embedding into every
		// template list page.
		builder.HasIndex(e => e.NormalizedDescriptionId);

		builder.HasData(
			new ItemTemplateEntity { Id = new Guid("cb05ed31-92a0-4c3d-bdbe-b9bd05183f38"), Name = "Gallon of Milk", DefaultCategory = "Groceries", DefaultSubcategory = "Dairy", DefaultUnitPrice = 4.99m, DefaultUnitPriceCurrency = Currency.USD, DefaultItemCode = "MILK-GAL" },
			new ItemTemplateEntity { Id = new Guid("33255f68-44df-4813-ad55-92260303c0ce"), Name = "Loaf of Bread", DefaultCategory = "Groceries", DefaultSubcategory = "Bakery", DefaultUnitPrice = 3.49m, DefaultUnitPriceCurrency = Currency.USD, DefaultItemCode = "BREAD" },
			new ItemTemplateEntity { Id = new Guid("3d11bbb7-ee69-4701-a45c-58bfc6458158"), Name = "Coffee (Medium)", DefaultCategory = "Dining", DefaultSubcategory = "Coffee Shop", DefaultUnitPrice = 4.50m, DefaultUnitPriceCurrency = Currency.USD, DefaultItemCode = "COFFEE-M" },
			new ItemTemplateEntity { Id = new Guid("a2de7840-ef72-42d5-b90f-9c25eb63f502"), Name = "Regular Unleaded Gas", DefaultCategory = "Transportation", DefaultSubcategory = "Gas", DefaultItemCode = "GAS-REG" }
		);

		builder.HasQueryFilter(e => e.DeletedAt == null);
	}
}
