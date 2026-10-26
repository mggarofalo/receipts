using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalCategoryAssistanceRequests : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.AddColumn<Guid>(
			name: "RequestScope",
			schema: "category_assistance",
			table: "Attempts",
			type: "uuid",
			nullable: false,
			defaultValue: Guid.Empty);

		migrationBuilder.CreateIndex(
			name: "IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~",
			schema: "category_assistance",
			table: "Attempts",
			columns: new[] { "RestoreGeneration", "SourceHash", "TaxonomyRevision", "Provider", "Model", "QuestionPolicyVersion", "ConnectionGeneration", "RequestScope" },
			unique: true);

		migrationBuilder.CreateTable(
			name: "HistoricalRequests",
			schema: "category_assistance",
			columns: table => new
			{
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", maxLength: 450, nullable: false),
				RequestHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				GroupsJson = table.Column<string>(type: "text", nullable: false),
				ItemCount = table.Column<int>(type: "integer", nullable: false),
				SettingsRevision = table.Column<Guid>(type: "uuid", nullable: false),
				RestoreGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				TaxonomyRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ConnectionGeneration = table.Column<Guid>(type: "uuid", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_HistoricalRequests", x => x.OperationId);
			});

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalRequests_Actor_CreatedAt",
			schema: "category_assistance",
			table: "HistoricalRequests",
			columns: new[] { "Actor", "CreatedAt" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Rollback cannot discard enrolled requests or scoped attempt decisions.
		migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM category_assistance."HistoricalRequests") OR EXISTS (
                        SELECT 1 FROM category_assistance."Attempts"
                        WHERE "RequestScope" <> '00000000-0000-0000-0000-000000000000'::uuid
                    ) THEN
                        RAISE EXCEPTION 'Cannot revert historical assistance requests while request history exists';
                    END IF;
                END $$;
                """);
		migrationBuilder.DropTable(
			name: "HistoricalRequests",
			schema: "category_assistance");
		migrationBuilder.DropIndex(
			name: "IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~",
			schema: "category_assistance",
			table: "Attempts");
		migrationBuilder.DropColumn(
			name: "RequestScope",
			schema: "category_assistance",
			table: "Attempts");
		migrationBuilder.CreateIndex(
			name: "IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~",
			schema: "category_assistance",
			table: "Attempts",
			columns: new[] { "RestoreGeneration", "SourceHash", "TaxonomyRevision", "Provider", "Model", "QuestionPolicyVersion", "ConnectionGeneration" },
			unique: true);
	}
}
