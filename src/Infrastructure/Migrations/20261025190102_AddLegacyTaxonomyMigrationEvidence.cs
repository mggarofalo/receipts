using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddLegacyTaxonomyMigrationEvidence : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "LegacyCrosswalks",
			schema: "taxonomy",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				SourceTable = table.Column<string>(type: "text", nullable: false),
				SourceId = table.Column<Guid>(type: "uuid", nullable: false),
				TargetId = table.Column<Guid>(type: "uuid", nullable: false),
				EvidenceJson = table.Column<string>(type: "text", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_LegacyCrosswalks", x => new { x.Actor, x.OperationId, x.SourceTable, x.SourceId });
			});

		migrationBuilder.CreateTable(
			name: "LegacyItemOutcomes",
			schema: "taxonomy",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptItemId = table.Column<Guid>(type: "uuid", nullable: false),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
				Resolution = table.Column<string>(type: "text", nullable: false),
				EvidenceJson = table.Column<string>(type: "text", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_LegacyItemOutcomes", x => new { x.Actor, x.OperationId, x.ReceiptItemId });
				table.ForeignKey(
					name: "FK_LegacyItemOutcomes_Assignments_AssignmentId",
					column: x => x.AssignmentId,
					principalSchema: "taxonomy",
					principalTable: "Assignments",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "LegacyMigrations",
			schema: "taxonomy",
			columns: table => new
			{
				Actor = table.Column<string>(type: "text", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				OperationKind = table.Column<string>(type: "text", nullable: false),
				SourceDataset = table.Column<string>(type: "text", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				ObservedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				PlanJson = table.Column<string>(type: "text", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_LegacyMigrations", x => new { x.Actor, x.OperationId, x.OperationKind });
				table.ForeignKey(
					name: "FK_LegacyMigrations_Revisions_RevisionId",
					column: x => x.RevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_LegacyItemOutcomes_AssignmentId",
			schema: "taxonomy",
			table: "LegacyItemOutcomes",
			column: "AssignmentId");

		migrationBuilder.CreateIndex(
			name: "IX_LegacyMigrations_RevisionId",
			schema: "taxonomy",
			table: "LegacyMigrations",
			column: "RevisionId",
			unique: true);
		migrationBuilder.Sql("""
                CREATE TRIGGER immutable_legacy_migrations BEFORE UPDATE OR DELETE ON taxonomy."LegacyMigrations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_legacy_crosswalks BEFORE UPDATE OR DELETE ON taxonomy."LegacyCrosswalks"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                CREATE TRIGGER immutable_legacy_item_outcomes BEFORE UPDATE OR DELETE ON taxonomy."LegacyItemOutcomes"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Legacy migration evidence is retained; restore a verified backup explicitly instead.'; END $$;");
	}
}
