using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class SeedDevelopmentData : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.InsertData(
			table: "Categories",
			columns: ["Id", "DeletedAt", "DeletedByApiKeyId", "DeletedByUserId", "Description", "Name"],
			values: new object[,]
			{
				{ new Guid("3a131ca1-3300-4cde-b7ee-24704934feea"), null, null, null, "Gas, transit, parking, and rideshare", "Transportation" },
				{ new Guid("705da6c5-6fb6-4b3c-aef1-f42e5136a499"), null, null, null, "Electric, water, internet, and phone", "Utilities" },
				{ new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), null, null, null, "Food and household supplies", "Groceries" },
				{ new Guid("92eae007-7d82-492c-9370-ff64873cc63a"), null, null, null, "Clothing, electronics, and general retail", "Shopping" },
				{ new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"), null, null, null, "Restaurants, takeout, and delivery", "Dining" }
			});

		migrationBuilder.InsertData(
			table: "ItemTemplates",
			columns: ["Id", "DefaultCategory", "DefaultItemCode", "DefaultPricingMode", "DefaultSubcategory", "DefaultUnitPrice", "DefaultUnitPriceCurrency", "DeletedAt", "DeletedByApiKeyId", "DeletedByUserId", "Description", "Name"],
			values: new object[,]
			{
				{ new Guid("33255f68-44df-4813-ad55-92260303c0ce"), "Groceries", "BREAD", "quantity", "Bakery", 3.49m, "USD", null, null, null, null, "Loaf of Bread" },
				{ new Guid("3d11bbb7-ee69-4701-a45c-58bfc6458158"), "Dining", "COFFEE-M", "flat", "Coffee Shop", 4.50m, "USD", null, null, null, null, "Coffee (Medium)" },
				{ new Guid("a2de7840-ef72-42d5-b90f-9c25eb63f502"), "Transportation", "GAS-REG", "quantity", "Gas", null, null, null, null, null, null, "Regular Unleaded Gas" },
				{ new Guid("cb05ed31-92a0-4c3d-bdbe-b9bd05183f38"), "Groceries", "MILK-GAL", "quantity", "Dairy", 4.99m, "USD", null, null, null, null, "Gallon of Milk" }
			});

		migrationBuilder.InsertData(
			table: "Subcategories",
			columns: ["Id", "CategoryId", "DeletedAt", "DeletedByApiKeyId", "DeletedByUserId", "Description", "Name"],
			values: new object[,]
			{
				{ new Guid("079d5267-8091-460b-86d4-7b2565b8bb25"), new Guid("3a131ca1-3300-4cde-b7ee-24704934feea"), null, null, null, null, "Parking" },
				{ new Guid("2ba877ec-9581-4927-aaea-729a778fb8ae"), new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"), null, null, null, null, "Fast Food" },
				{ new Guid("2e5bef54-3b06-4d8d-8f53-7f94e8d88e99"), new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), null, null, null, null, "Bakery" },
				{ new Guid("7bb01875-3807-44a2-b8fb-3459a514d81f"), new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), null, null, null, null, "Meat & Seafood" },
				{ new Guid("8fd8809c-7081-4997-925c-49c0a244a4e4"), new Guid("3a131ca1-3300-4cde-b7ee-24704934feea"), null, null, null, null, "Gas" },
				{ new Guid("9c90a29d-546c-4ab8-a5f7-4168b675cda8"), new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"), null, null, null, null, "Coffee Shop" },
				{ new Guid("ad045a79-d5a1-404e-b7a8-c475680681f1"), new Guid("92eae007-7d82-492c-9370-ff64873cc63a"), null, null, null, null, "Electronics" },
				{ new Guid("af940cc4-9838-46ac-8c30-3573d876ae47"), new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"), null, null, null, null, "Sit-Down Restaurant" },
				{ new Guid("bdc5740a-352e-44a9-bd1d-a85f5b0cb833"), new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), null, null, null, "Fruits and vegetables", "Produce" },
				{ new Guid("c9205933-8cc6-4dfc-b08f-46ba221036fa"), new Guid("705da6c5-6fb6-4b3c-aef1-f42e5136a499"), null, null, null, null, "Internet" },
				{ new Guid("d00f80ca-5e7a-44f8-bf97-b44fccb430b1"), new Guid("92eae007-7d82-492c-9370-ff64873cc63a"), null, null, null, null, "Clothing" },
				{ new Guid("d8052b39-045a-4c1f-b0a5-3b94920fe010"), new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"), null, null, null, "Milk, cheese, yogurt", "Dairy" },
				{ new Guid("f7c4d461-ed9f-421c-94d3-32e9a55d6bb0"), new Guid("705da6c5-6fb6-4b3c-aef1-f42e5136a499"), null, null, null, null, "Electric" }
			});
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DeleteData(
			table: "ItemTemplates",
			keyColumn: "Id",
			keyValue: new Guid("33255f68-44df-4813-ad55-92260303c0ce"));

		migrationBuilder.DeleteData(
			table: "ItemTemplates",
			keyColumn: "Id",
			keyValue: new Guid("3d11bbb7-ee69-4701-a45c-58bfc6458158"));

		migrationBuilder.DeleteData(
			table: "ItemTemplates",
			keyColumn: "Id",
			keyValue: new Guid("a2de7840-ef72-42d5-b90f-9c25eb63f502"));

		migrationBuilder.DeleteData(
			table: "ItemTemplates",
			keyColumn: "Id",
			keyValue: new Guid("cb05ed31-92a0-4c3d-bdbe-b9bd05183f38"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("079d5267-8091-460b-86d4-7b2565b8bb25"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("2ba877ec-9581-4927-aaea-729a778fb8ae"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("2e5bef54-3b06-4d8d-8f53-7f94e8d88e99"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("7bb01875-3807-44a2-b8fb-3459a514d81f"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("8fd8809c-7081-4997-925c-49c0a244a4e4"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("9c90a29d-546c-4ab8-a5f7-4168b675cda8"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("ad045a79-d5a1-404e-b7a8-c475680681f1"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("af940cc4-9838-46ac-8c30-3573d876ae47"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("bdc5740a-352e-44a9-bd1d-a85f5b0cb833"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("c9205933-8cc6-4dfc-b08f-46ba221036fa"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("d00f80ca-5e7a-44f8-bf97-b44fccb430b1"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("d8052b39-045a-4c1f-b0a5-3b94920fe010"));

		migrationBuilder.DeleteData(
			table: "Subcategories",
			keyColumn: "Id",
			keyValue: new Guid("f7c4d461-ed9f-421c-94d3-32e9a55d6bb0"));

		migrationBuilder.DeleteData(
			table: "Categories",
			keyColumn: "Id",
			keyValue: new Guid("3a131ca1-3300-4cde-b7ee-24704934feea"));

		migrationBuilder.DeleteData(
			table: "Categories",
			keyColumn: "Id",
			keyValue: new Guid("705da6c5-6fb6-4b3c-aef1-f42e5136a499"));

		migrationBuilder.DeleteData(
			table: "Categories",
			keyColumn: "Id",
			keyValue: new Guid("83a7f4ea-f771-40b3-850f-35b90a3bd05e"));

		migrationBuilder.DeleteData(
			table: "Categories",
			keyColumn: "Id",
			keyValue: new Guid("92eae007-7d82-492c-9370-ff64873cc63a"));

		migrationBuilder.DeleteData(
			table: "Categories",
			keyColumn: "Id",
			keyValue: new Guid("e37ce004-56ea-4a33-8983-55a9552d05be"));
	}
}
