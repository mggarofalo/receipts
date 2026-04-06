using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddYnabIntegration : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "ProcessedImagePath",
			table: "Receipts",
			type: "text",
			maxLength: 1024,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "character varying(1024)",
			oldMaxLength: 1024,
			oldNullable: true);

		migrationBuilder.AlterColumn<string>(
			name: "OriginalImagePath",
			table: "Receipts",
			type: "text",
			maxLength: 1024,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "character varying(1024)",
			oldMaxLength: 1024,
			oldNullable: true);

		migrationBuilder.CreateTable(
			name: "YnabAccountMappings",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptsAccountId = table.Column<Guid>(type: "uuid", nullable: false),
				YnabAccountId = table.Column<string>(type: "text", maxLength: 256, nullable: false),
				YnabAccountName = table.Column<string>(type: "text", maxLength: 500, nullable: false),
				YnabBudgetId = table.Column<string>(type: "text", maxLength: 256, nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabAccountMappings", x => x.Id);
				table.ForeignKey(
					name: "FK_YnabAccountMappings_Accounts_ReceiptsAccountId",
					column: x => x.ReceiptsAccountId,
					principalTable: "Accounts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "YnabCategoryMappings",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptsCategory = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				YnabCategoryId = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				YnabCategoryName = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				YnabCategoryGroupName = table.Column<string>(type: "text", maxLength: 200, nullable: false),
				YnabBudgetId = table.Column<string>(type: "text", maxLength: 100, nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabCategoryMappings", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "YnabSelectedBudgets",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				BudgetId = table.Column<string>(type: "text", maxLength: 36, nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabSelectedBudgets", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "YnabSyncRecords",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				LocalTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
				YnabTransactionId = table.Column<string>(type: "text", nullable: true),
				YnabBudgetId = table.Column<string>(type: "text", nullable: false),
				YnabAccountId = table.Column<string>(type: "text", nullable: true),
				SyncType = table.Column<string>(type: "text", nullable: false),
				SyncStatus = table.Column<string>(type: "text", nullable: false),
				SyncedAtUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				LastError = table.Column<string>(type: "text", maxLength: 2000, nullable: true),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabSyncRecords", x => x.Id);
				table.ForeignKey(
					name: "FK_YnabSyncRecords_Transactions_LocalTransactionId",
					column: x => x.LocalTransactionId,
					principalTable: "Transactions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_YnabAccountMappings_ReceiptsAccountId",
			table: "YnabAccountMappings",
			column: "ReceiptsAccountId",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_YnabCategoryMappings_ReceiptsCategory",
			table: "YnabCategoryMappings",
			column: "ReceiptsCategory",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncRecords_LocalTransactionId_SyncType",
			table: "YnabSyncRecords",
			columns: ["LocalTransactionId", "SyncType"],
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "YnabAccountMappings");

		migrationBuilder.DropTable(
			name: "YnabCategoryMappings");

		migrationBuilder.DropTable(
			name: "YnabSelectedBudgets");

		migrationBuilder.DropTable(
			name: "YnabSyncRecords");

		migrationBuilder.AlterColumn<string>(
			name: "ProcessedImagePath",
			table: "Receipts",
			type: "character varying(1024)",
			maxLength: 1024,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "text",
			oldMaxLength: 1024,
			oldNullable: true);

		migrationBuilder.AlterColumn<string>(
			name: "OriginalImagePath",
			table: "Receipts",
			type: "character varying(1024)",
			maxLength: 1024,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "text",
			oldMaxLength: 1024,
			oldNullable: true);
	}
}
