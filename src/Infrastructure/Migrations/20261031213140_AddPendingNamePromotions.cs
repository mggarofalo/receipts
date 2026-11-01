using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddPendingNamePromotions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "PendingNamePromotions",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				CreatedBy = table.Column<string>(type: "text", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				MinimumPurchases = table.Column<int>(type: "integer", nullable: false),
				PurchaseCount = table.Column<int>(type: "integer", nullable: false),
				UndoneAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				UndoneBy = table.Column<string>(type: "text", nullable: true),
				RestoredCount = table.Column<int>(type: "integer", nullable: true),
				KeptCount = table.Column<int>(type: "integer", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_PendingNamePromotions", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "PendingNamePromotionEntries",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
				NormalizedDescriptionId = table.Column<Guid>(type: "uuid", nullable: false),
				RevisionAfter = table.Column<Guid>(type: "uuid", nullable: false),
				Restored = table.Column<bool>(type: "boolean", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_PendingNamePromotionEntries", x => x.Id);
				table.ForeignKey(
					name: "FK_PendingNamePromotionEntries_PendingNamePromotions_Promotion~",
					column: x => x.PromotionId,
					principalSchema: "matching",
					principalTable: "PendingNamePromotions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_PendingNamePromotionEntries_PromotionId_NormalizedDescripti~",
			schema: "matching",
			table: "PendingNamePromotionEntries",
			columns: new[] { "PromotionId", "NormalizedDescriptionId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_PendingNamePromotions_CreatedAt",
			schema: "matching",
			table: "PendingNamePromotions",
			column: "CreatedAt");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "PendingNamePromotionEntries",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "PendingNamePromotions",
			schema: "matching");
	}
}
