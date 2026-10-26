using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddApprovedCategoryMappings : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "CategoryMappingRevisions",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
				SourceFingerprint = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Location = table.Column<string>(type: "text", maxLength: 1000, nullable: false),
				Description = table.Column<string>(type: "text", maxLength: 2000, nullable: false),
				ItemCode = table.Column<string>(type: "text", maxLength: 200, nullable: true),
				CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
				TaxonomyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				SemanticFingerprint = table.Column<string>(type: "text", maxLength: 64, nullable: true),
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				RequestHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Reason = table.Column<string>(type: "text", maxLength: 4000, nullable: false),
				RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_CategoryMappingRevisions", x => x.Id);
				table.ForeignKey(
					name: "FK_CategoryMappingRevisions_CategoryMappingRevisions_PreviousR~",
					column: x => x.PreviousRevisionId,
					principalSchema: "taxonomy",
					principalTable: "CategoryMappingRevisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_CategoryMappingRevisions_Nodes_TaxonomyRevisionId_CategoryId",
					columns: x => new { x.TaxonomyRevisionId, x.CategoryId },
					principalSchema: "taxonomy",
					principalTable: "Nodes",
					principalColumns: new[] { "RevisionId", "CategoryId" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_CategoryMappingRevisions_Revisions_TaxonomyRevisionId",
					column: x => x.TaxonomyRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "CategoryMappingHeads",
			schema: "taxonomy",
			columns: table => new
			{
				SourceFingerprint = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				RevisionId = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_CategoryMappingHeads", x => x.SourceFingerprint);
				table.ForeignKey(
					name: "FK_CategoryMappingHeads_CategoryMappingRevisions_RevisionId",
					column: x => x.RevisionId,
					principalSchema: "taxonomy",
					principalTable: "CategoryMappingRevisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_CategoryMappingHeads_RevisionId",
			schema: "taxonomy",
			table: "CategoryMappingHeads",
			column: "RevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_CategoryMappingRevisions_Actor_OperationId",
			schema: "taxonomy",
			table: "CategoryMappingRevisions",
			columns: new[] { "Actor", "OperationId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_CategoryMappingRevisions_PreviousRevisionId",
			schema: "taxonomy",
			table: "CategoryMappingRevisions",
			column: "PreviousRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_CategoryMappingRevisions_SourceFingerprint",
			schema: "taxonomy",
			table: "CategoryMappingRevisions",
			column: "SourceFingerprint");

		migrationBuilder.CreateIndex(
			name: "IX_CategoryMappingRevisions_TaxonomyRevisionId_CategoryId",
			schema: "taxonomy",
			table: "CategoryMappingRevisions",
			columns: new[] { "TaxonomyRevisionId", "CategoryId" });

		migrationBuilder.Sql("""
                CREATE TRIGGER immutable_category_mapping_revisions
                    BEFORE UPDATE OR DELETE ON taxonomy."CategoryMappingRevisions"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Mapping history is retained; restore a verified backup explicitly instead.'; END $$;");
	}
}
