using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class WidenReceiptItemQuantityAndUnitPricePrecision : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<decimal>(
			name: "UnitPrice",
			schema: "receipts",
			table: "ReceiptItems",
			type: "numeric(18,4)",
			nullable: false,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,2)");

		migrationBuilder.AlterColumn<decimal>(
			name: "Quantity",
			schema: "receipts",
			table: "ReceiptItems",
			type: "numeric(18,4)",
			nullable: false,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,2)");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<decimal>(
			name: "UnitPrice",
			schema: "receipts",
			table: "ReceiptItems",
			type: "numeric(18,2)",
			nullable: false,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,4)");

		migrationBuilder.AlterColumn<decimal>(
			name: "Quantity",
			schema: "receipts",
			table: "ReceiptItems",
			type: "numeric(18,2)",
			nullable: false,
			oldClrType: typeof(decimal),
			oldType: "numeric(18,4)");
	}
}
