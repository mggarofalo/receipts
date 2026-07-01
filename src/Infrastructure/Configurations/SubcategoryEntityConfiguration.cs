using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

public class SubcategoryEntityConfiguration : IEntityTypeConfiguration<SubcategoryEntity>
{
	public void Configure(EntityTypeBuilder<SubcategoryEntity> builder)
	{
		builder.ToTable("Subcategories", "library");

		builder.HasKey(e => e.Id);

		builder.Property(e => e.Id)
			.IsRequired()
			.ValueGeneratedOnAdd();

		builder.HasIndex(e => new { e.CategoryId, e.Name })
			.IsUnique()
			.HasFilter("\"DeletedAt\" IS NULL");

		builder.Navigation(e => e.Category)
			.AutoInclude();

		builder.HasQueryFilter(e => e.DeletedAt == null);

		Guid groceries = new("83a7f4ea-f771-40b3-850f-35b90a3bd05e");
		Guid dining = new("e37ce004-56ea-4a33-8983-55a9552d05be");
		Guid transportation = new("3a131ca1-3300-4cde-b7ee-24704934feea");
		Guid shopping = new("92eae007-7d82-492c-9370-ff64873cc63a");
		Guid utilities = new("705da6c5-6fb6-4b3c-aef1-f42e5136a499");

		builder.HasData(
			new SubcategoryEntity { Id = new Guid("bdc5740a-352e-44a9-bd1d-a85f5b0cb833"), CategoryId = groceries, Name = "Produce", Description = "Fruits and vegetables", IsActive = true },
			new SubcategoryEntity { Id = new Guid("d8052b39-045a-4c1f-b0a5-3b94920fe010"), CategoryId = groceries, Name = "Dairy", Description = "Milk, cheese, yogurt", IsActive = true },
			new SubcategoryEntity { Id = new Guid("7bb01875-3807-44a2-b8fb-3459a514d81f"), CategoryId = groceries, Name = "Meat & Seafood", IsActive = true },
			new SubcategoryEntity { Id = new Guid("2e5bef54-3b06-4d8d-8f53-7f94e8d88e99"), CategoryId = groceries, Name = "Bakery", IsActive = true },
			new SubcategoryEntity { Id = new Guid("2ba877ec-9581-4927-aaea-729a778fb8ae"), CategoryId = dining, Name = "Fast Food", IsActive = true },
			new SubcategoryEntity { Id = new Guid("af940cc4-9838-46ac-8c30-3573d876ae47"), CategoryId = dining, Name = "Sit-Down Restaurant", IsActive = true },
			new SubcategoryEntity { Id = new Guid("9c90a29d-546c-4ab8-a5f7-4168b675cda8"), CategoryId = dining, Name = "Coffee Shop", IsActive = true },
			new SubcategoryEntity { Id = new Guid("8fd8809c-7081-4997-925c-49c0a244a4e4"), CategoryId = transportation, Name = "Gas", IsActive = true },
			new SubcategoryEntity { Id = new Guid("079d5267-8091-460b-86d4-7b2565b8bb25"), CategoryId = transportation, Name = "Parking", IsActive = true },
			new SubcategoryEntity { Id = new Guid("ad045a79-d5a1-404e-b7a8-c475680681f1"), CategoryId = shopping, Name = "Electronics", IsActive = true },
			new SubcategoryEntity { Id = new Guid("d00f80ca-5e7a-44f8-bf97-b44fccb430b1"), CategoryId = shopping, Name = "Clothing", IsActive = true },
			new SubcategoryEntity { Id = new Guid("f7c4d461-ed9f-421c-94d3-32e9a55d6bb0"), CategoryId = utilities, Name = "Electric", IsActive = true },
			new SubcategoryEntity { Id = new Guid("c9205933-8cc6-4dfc-b08f-46ba221036fa"), CategoryId = utilities, Name = "Internet", IsActive = true }
		);
	}
}
