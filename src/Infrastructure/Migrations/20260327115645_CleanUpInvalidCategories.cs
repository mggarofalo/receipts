using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class CleanUpInvalidCategories : Migration
{
	/// <summary>
	/// The set of valid category names that should remain in the Categories table.
	/// Any category whose name is not in this list is considered invalid (e.g. store
	/// names like "Costco" or "Walmart" that were entered via the old allowCustom
	/// combobox behavior) and will be cleaned up.
	/// </summary>
	private static readonly string[] ValidCategoryNames =
	[
		"Groceries",
		"Dining",
		"Transportation",
		"Shopping",
		"Utilities",
		"Uncategorized",
	];

	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// 1. Insert the "Uncategorized" seed category (EF Core HasData).
		migrationBuilder.InsertData(
			table: "Categories",
			columns: ["Id", "Description", "Name"],
			values: [new Guid("f0e7a123-9b56-4d3a-8c1e-2a5b7d9f4e6c"), "Default category for items without a valid category", "Uncategorized"]);

		string validList = string.Join(", ", Array.ConvertAll(ValidCategoryNames, n => $"'{n.Replace("'", "''")}'"));

		// 2. Reassign receipt items whose Category string doesn't match a valid
		//    category name to "Uncategorized". Include soft-deleted items so that
		//    restored receipts don't re-emerge with invalid categories.
		migrationBuilder.Sql(
			$"UPDATE \"ReceiptItems\" SET \"Category\" = 'Uncategorized'" +
			$" WHERE \"Category\" NOT IN ({validList})");

		// 3. Reassign item templates whose DefaultCategory doesn't match a valid
		//    category name. Include soft-deleted templates for the same reason.
		migrationBuilder.Sql(
			$"UPDATE \"ItemTemplates\" SET \"DefaultCategory\" = 'Uncategorized'" +
			$" WHERE \"DefaultCategory\" IS NOT NULL" +
			$" AND \"DefaultCategory\" NOT IN ({validList})");

		// 4. Delete subcategories that belong to invalid categories.
		migrationBuilder.Sql(
			$"DELETE FROM \"Subcategories\" WHERE \"CategoryId\" IN (" +
			$"SELECT \"Id\" FROM \"Categories\" WHERE \"Name\" NOT IN ({validList}))");

		// 5. Delete the invalid categories themselves.
		migrationBuilder.Sql(
			$"DELETE FROM \"Categories\" WHERE \"Name\" NOT IN ({validList})");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// The data cleanup is not reversible — we cannot restore the original
		// invalid category names or re-associate receipt items with them.
		// Intentionally keeping the "Uncategorized" seed row: deleting it would
		// leave orphaned references in ReceiptItems and ItemTemplates that were
		// reassigned to "Uncategorized" during Up().
	}
}
