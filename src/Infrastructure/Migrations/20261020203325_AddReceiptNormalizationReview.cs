using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddReceiptNormalizationReview : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "NormalizationExclusionReason",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationReviewDecisionId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "NormalizationReviewReason",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 500,
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "NormalizationReviewState",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 32,
			nullable: false,
			defaultValue: "NeedsReview");

		migrationBuilder.AddColumn<string>(
			name: "NormalizationSourceHash",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 64,
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "ReviewMappingRevision",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ReviewedAt",
			schema: "receipts",
			table: "ReceiptItems",
			type: "timestamptz",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "ReviewedBy",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 450,
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "ReviewedSourceHash",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 64,
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "ExclusionReason",
			schema: "matching",
			table: "NormalizationSourceMappings",
			type: "text",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "NormalizationReviewWork",
			schema: "matching",
			columns: table => new
			{
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				SourceHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				SourceRevision = table.Column<Guid>(type: "uuid", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationReviewWork", x => x.ItemId);
				table.ForeignKey(
					name: "FK_NormalizationReviewWork_ReceiptItems_ItemId",
					column: x => x.ItemId,
					principalSchema: "receipts",
					principalTable: "ReceiptItems",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "ReceiptWriteRequests",
			schema: "receipts",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				RequestId = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Kind = table.Column<string>(type: "text", maxLength: 32, nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemIds = table.Column<Guid[]>(type: "uuid[]", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ReceiptWriteRequests", x => new { x.Actor, x.RequestId });
			});

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationReviewWork_SourceHash_CreatedAt",
			schema: "matching",
			table: "NormalizationReviewWork",
			columns: new[] { "SourceHash", "CreatedAt" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "NormalizationReviewWork",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "ReceiptWriteRequests",
			schema: "receipts");

		migrationBuilder.DropColumn(
			name: "NormalizationExclusionReason",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationReviewDecisionId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationReviewReason",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationReviewState",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationSourceHash",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "ReviewMappingRevision",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "ReviewedAt",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "ReviewedBy",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "ReviewedSourceHash",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "ExclusionReason",
			schema: "matching",
			table: "NormalizationSourceMappings");
	}
}
