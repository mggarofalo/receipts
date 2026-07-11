using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class RestrictTransactionAccountIdOnDelete : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions");

		migrationBuilder.AddForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions",
			column: "AccountId",
			principalSchema: "library",
			principalTable: "Accounts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions");

		migrationBuilder.AddForeignKey(
			name: "FK_Transactions_Accounts_AccountId",
			schema: "receipts",
			table: "Transactions",
			column: "AccountId",
			principalSchema: "library",
			principalTable: "Accounts",
			principalColumn: "Id",
			onDelete: ReferentialAction.Cascade);
	}
}
