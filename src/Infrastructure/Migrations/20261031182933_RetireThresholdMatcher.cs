using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class RetireThresholdMatcher : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "NormalizedDescriptionSettings",
			schema: "matching");

		migrationBuilder.DropIndex(
			name: "IX_ReceiptItems_NormalizedDescriptionMatchScore",
			schema: "receipts",
			table: "ReceiptItems");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "NormalizedDescriptionSettings",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				AutoAcceptThreshold = table.Column<double>(type: "double precision", nullable: false),
				PendingReviewThreshold = table.Column<double>(type: "double precision", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizedDescriptionSettings", x => x.Id);
			});

		migrationBuilder.InsertData(
			schema: "matching",
			table: "NormalizedDescriptionSettings",
			columns: new[] { "Id", "AutoAcceptThreshold", "PendingReviewThreshold", "UpdatedAt" },
			values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), 0.81000000000000005, 0.68000000000000005, new DateTimeOffset(new DateTime(2026, 4, 19, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

		migrationBuilder.CreateIndex(
			name: "IX_ReceiptItems_NormalizedDescriptionMatchScore",
			schema: "receipts",
			table: "ReceiptItems",
			column: "NormalizedDescriptionMatchScore");
	}
}
