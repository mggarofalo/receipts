using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddYnabSyncEventTable : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "YnabSyncEvents",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				EventType = table.Column<string>(type: "text", nullable: false),
				Outcome = table.Column<string>(type: "text", nullable: false),
				LocalTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
				YnabBudgetId = table.Column<string>(type: "text", maxLength: 64, nullable: true),
				YnabTransactionId = table.Column<string>(type: "text", maxLength: 64, nullable: true),
				ErrorMessage = table.Column<string>(type: "text", maxLength: 2000, nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabSyncEvents", x => x.Id);
			});

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncEvents_OccurredAt",
			table: "YnabSyncEvents",
			column: "OccurredAt",
			descending: new bool[0]);

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncEvents_ReceiptId",
			table: "YnabSyncEvents",
			column: "ReceiptId");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "YnabSyncEvents");
	}
}
