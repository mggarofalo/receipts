using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class CategoryEntityConfiguration : IEntityTypeConfiguration<CategoryEntity>
{
	public void Configure(EntityTypeBuilder<CategoryEntity> builder)
	{
		builder.ToTable("Categories", "library");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.HasIndex(e => e.Name)
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		builder.HasQueryFilter(e => e.DeletedAt == null);

		builder.HasData(
			new CategoryEntity { Id = new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), Name = "Groceries", Description = "Food and household supplies", IsActive = true },
			new CategoryEntity { Id = new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"), Name = "Dining", Description = "Restaurants, takeout, and delivery", IsActive = true },
			new CategoryEntity { Id = new Guid("3a131ca1-3300-4cde-b7ee-24704934feea"), Name = "Transportation", Description = "Gas, transit, parking, and rideshare", IsActive = true },
			new CategoryEntity { Id = new Guid("92eae007-7d82-492c-9370-ff64873cc63a"), Name = "Shopping", Description = "Clothing, electronics, and general retail", IsActive = true },
			new CategoryEntity { Id = new Guid("705da6c5-6fb6-4b3c-aef1-f42e5136a499"), Name = "Utilities", Description = "Electric, water, internet, and phone", IsActive = true },
			new CategoryEntity { Id = new Guid("f0e7a123-9b56-4d3a-8c1e-2a5b7d9f4e6c"), Name = "Uncategorized", Description = "Default category for items without a valid category", IsActive = true }
		);
	}
}
