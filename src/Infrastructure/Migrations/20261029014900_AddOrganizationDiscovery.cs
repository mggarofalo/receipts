using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddOrganizationDiscovery : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.EnsureSchema(
			name: "organization");

		migrationBuilder.CreateTable(
			name: "DirtyItems",
			schema: "organization",
			columns: table => new
			{
				ItemId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_DirtyItems", x => x.ItemId);
			});

		migrationBuilder.CreateTable(
			name: "DirtySources",
			schema: "organization",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				FormatVersion = table.Column<int>(type: "integer", nullable: false),
				Kind = table.Column<string>(type: "text", nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				Description = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				Cursor = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_DirtySources", x => x.Id);
				table.CheckConstraint("CK_OrganizationDirtySource_Kind", "\"Kind\" IN ('raw','normalized')");
				table.CheckConstraint("CK_OrganizationDirtySource_Version", "\"FormatVersion\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "Runs",
			schema: "organization",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				FormatVersion = table.Column<int>(type: "integer", nullable: false),
				PolicyRevision = table.Column<Guid>(type: "uuid", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				TaxonomyRevision = table.Column<Guid>(type: "uuid", nullable: false),
				Active = table.Column<bool>(type: "boolean", nullable: false),
				State = table.Column<string>(type: "text", nullable: false),
				Cursor = table.Column<Guid>(type: "uuid", nullable: true),
				HighWater = table.Column<Guid>(type: "uuid", nullable: true),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Runs", x => x.Id);
				table.CheckConstraint("CK_OrganizationRun_State", "(\"Active\" AND \"State\" IN ('scanning','watching')) OR (NOT \"Active\" AND \"State\" = 'stale')");
				table.CheckConstraint("CK_OrganizationRun_Version", "\"FormatVersion\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "Sources",
			schema: "organization",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				RunId = table.Column<Guid>(type: "uuid", nullable: false),
				FormatVersion = table.Column<int>(type: "integer", nullable: false),
				RawHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Store = table.Column<string>(type: "text", nullable: false, collation: "C"),
				Description = table.Column<string>(type: "text", nullable: false, collation: "C"),
				ItemCode = table.Column<string>(type: "text", nullable: true, collation: "C"),
				NormalizedHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				NormalizedStore = table.Column<string>(type: "text", nullable: false),
				NormalizedDescription = table.Column<string>(type: "text", nullable: false),
				NormalizedItemCode = table.Column<string>(type: "text", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Sources", x => x.Id);
				table.UniqueConstraint("AK_Sources_RunId_Id", x => new { x.RunId, x.Id });
				table.CheckConstraint("CK_OrganizationSource_Version", "\"FormatVersion\" = 1");
				table.ForeignKey(
					name: "FK_Sources_Runs_RunId",
					column: x => x.RunId,
					principalSchema: "organization",
					principalTable: "Runs",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "Items",
			schema: "organization",
			columns: table => new
			{
				RunId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				SourceId = table.Column<Guid>(type: "uuid", nullable: true),
				FormatVersion = table.Column<int>(type: "integer", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ExpectedAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				PreviousCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
				State = table.Column<string>(type: "text", nullable: false),
				ObservedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_Items", x => new { x.RunId, x.ItemId });
				table.CheckConstraint("CK_OrganizationItem_State", "\"State\" IN ('observed','protected','unavailable','invalid_source')");
				table.CheckConstraint("CK_OrganizationItem_Version", "\"FormatVersion\" = 1");
				table.ForeignKey(
					name: "FK_Items_Runs_RunId",
					column: x => x.RunId,
					principalSchema: "organization",
					principalTable: "Runs",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_Items_Sources_RunId_SourceId",
					columns: x => new { x.RunId, x.SourceId },
					principalSchema: "organization",
					principalTable: "Sources",
					principalColumns: new[] { "RunId", "Id" },
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_Items_RunId_SourceId",
			schema: "organization",
			table: "Items",
			columns: new[] { "RunId", "SourceId" });

		migrationBuilder.CreateIndex(
			name: "IX_Items_RunId_State_ItemId",
			schema: "organization",
			table: "Items",
			columns: new[] { "RunId", "State", "ItemId" });

		migrationBuilder.CreateIndex(
			name: "IX_Runs_Active",
			schema: "organization",
			table: "Runs",
			column: "Active",
			unique: true,
			filter: "\"Active\"");

		migrationBuilder.CreateIndex(
			name: "IX_Sources_RunId_RawHash",
			schema: "organization",
			table: "Sources",
			columns: new[] { "RunId", "RawHash" },
			unique: true);
		AddDiscoveryTriggers(migrationBuilder);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		RemoveDiscoveryTriggers(migrationBuilder);
		migrationBuilder.DropTable(
			name: "DirtyItems",
			schema: "organization");

		migrationBuilder.DropTable(
			name: "DirtySources",
			schema: "organization");

		migrationBuilder.DropTable(
			name: "Items",
			schema: "organization");

		migrationBuilder.DropTable(
			name: "Sources",
			schema: "organization");

		migrationBuilder.DropTable(
			name: "Runs",
			schema: "organization");
	}
}
