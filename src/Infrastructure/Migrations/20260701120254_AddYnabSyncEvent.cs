using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddYnabSyncEvent : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "YnabSyncEvents",
			schema: "ynab",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				UserId = table.Column<string>(type: "text", nullable: true),
				OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				EventType = table.Column<string>(type: "text", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
				TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
				HttpStatus = table.Column<int>(type: "integer", nullable: true),
				Success = table.Column<bool>(type: "boolean", nullable: false),
				ErrorMessage = table.Column<string>(type: "text", nullable: true),
				RequestId = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_YnabSyncEvents", x => x.Id);
			});

		migrationBuilder.CreateIndex(
			name: "IX_YnabSyncEvents_UserId_OccurredAt",
			schema: "ynab",
			table: "YnabSyncEvents",
			columns: new[] { "UserId", "OccurredAt" },
			descending: new[] { false, true });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "YnabSyncEvents",
			schema: "ynab");
	}
}
