using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAcceptedDuplicatePairs : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "AcceptedDuplicatePairs",
			schema: "receipts",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptIdA = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptIdB = table.Column<Guid>(type: "uuid", nullable: false),
				AcceptedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				DeletedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
				DeletedByUserId = table.Column<string>(type: "text", nullable: true),
				DeletedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
				CascadeDeletedByParentId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_AcceptedDuplicatePairs", x => x.Id);
				table.CheckConstraint("CK_AcceptedDuplicatePairs_CanonicalOrder", "\"ReceiptIdA\" < \"ReceiptIdB\"");
				table.ForeignKey(
					name: "FK_AcceptedDuplicatePairs_Receipts_ReceiptIdA",
					column: x => x.ReceiptIdA,
					principalSchema: "receipts",
					principalTable: "Receipts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "FK_AcceptedDuplicatePairs_Receipts_ReceiptIdB",
					column: x => x.ReceiptIdB,
					principalSchema: "receipts",
					principalTable: "Receipts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_AcceptedDuplicatePairs_ReceiptIdA",
			schema: "receipts",
			table: "AcceptedDuplicatePairs",
			column: "ReceiptIdA");

		migrationBuilder.CreateIndex(
			name: "IX_AcceptedDuplicatePairs_ReceiptIdA_ReceiptIdB",
			schema: "receipts",
			table: "AcceptedDuplicatePairs",
			columns: new[] { "ReceiptIdA", "ReceiptIdB" },
			unique: true,
			filter: "\"DeletedAt\" IS NULL");

		migrationBuilder.CreateIndex(
			name: "IX_AcceptedDuplicatePairs_ReceiptIdB",
			schema: "receipts",
			table: "AcceptedDuplicatePairs",
			column: "ReceiptIdB");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "AcceptedDuplicatePairs",
			schema: "receipts");
	}
}
