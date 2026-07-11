using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddReceiptDateIndex : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateIndex(
			name: "IX_Receipts_Date",
			schema: "receipts",
			table: "Receipts",
			column: "Date",
			filter: "\"DeletedAt\" IS NULL");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_Receipts_Date",
			schema: "receipts",
			table: "Receipts");
	}
}
