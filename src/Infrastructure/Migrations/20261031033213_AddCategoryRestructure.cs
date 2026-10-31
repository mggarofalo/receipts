using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddCategoryRestructure : Migration
{
	private const string ItemGuard = """
                CREATE OR REPLACE FUNCTION taxonomy.guard_historical_provenance_item() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE operation_kind text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND NEW."ApprovedMappingRevisionId" IS DISTINCT FROM OLD."ApprovedMappingRevisionId" THEN
                        RAISE EXCEPTION 'Historical provenance is immutable' USING ERRCODE = '23514';
                    END IF;
                    SELECT "Kind" INTO operation_kind FROM taxonomy."HistoricalOperations" WHERE "Id" = NEW."OperationId";
                    IF operation_kind IN ('ManualV1', 'JevSuggestionV1', 'RestructureV1') AND NEW."ApprovedMappingRevisionId" IS NOT NULL THEN
                        RAISE EXCEPTION 'Only approved mapping items carry mapping provenance' USING ERRCODE = '23514';
                    END IF;
                    IF operation_kind = 'ApprovedMappingV1' AND NOT EXISTS (
                        SELECT 1 FROM taxonomy."CategoryMappingRevisions" m
                        WHERE m."Id" = NEW."ApprovedMappingRevisionId" AND m."CategoryId" IS NOT NULL
                          AND m."CategoryId" = NEW."TargetCategoryId" AND m."SemanticFingerprint" IS NOT NULL
                          AND m."Location" COLLATE "C" = NEW."Store" COLLATE "C"
                          AND m."Description" COLLATE "C" = NEW."RawDescription" COLLATE "C"
                          AND m."ItemCode" COLLATE "C" IS NOT DISTINCT FROM NEW."ItemCode" COLLATE "C"
                    ) THEN
                        RAISE EXCEPTION 'Automatic historical item requires matching approved mapping evidence' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """;

	private const string PreviousItemGuard = """
                CREATE OR REPLACE FUNCTION taxonomy.guard_historical_provenance_item() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE operation_kind text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND NEW."ApprovedMappingRevisionId" IS DISTINCT FROM OLD."ApprovedMappingRevisionId" THEN
                        RAISE EXCEPTION 'Historical provenance is immutable' USING ERRCODE = '23514';
                    END IF;
                    SELECT "Kind" INTO operation_kind FROM taxonomy."HistoricalOperations" WHERE "Id" = NEW."OperationId";
                    IF operation_kind IN ('ManualV1', 'JevSuggestionV1') AND NEW."ApprovedMappingRevisionId" IS NOT NULL THEN
                        RAISE EXCEPTION 'Only approved mapping items carry mapping provenance' USING ERRCODE = '23514';
                    END IF;
                    IF operation_kind = 'ApprovedMappingV1' AND NOT EXISTS (
                        SELECT 1 FROM taxonomy."CategoryMappingRevisions" m
                        WHERE m."Id" = NEW."ApprovedMappingRevisionId" AND m."CategoryId" IS NOT NULL
                          AND m."CategoryId" = NEW."TargetCategoryId" AND m."SemanticFingerprint" IS NOT NULL
                          AND m."Location" COLLATE "C" = NEW."Store" COLLATE "C"
                          AND m."Description" COLLATE "C" = NEW."RawDescription" COLLATE "C"
                          AND m."ItemCode" COLLATE "C" IS NOT DISTINCT FROM NEW."ItemCode" COLLATE "C"
                    ) THEN
                        RAISE EXCEPTION 'Automatic historical item requires matching approved mapping evidence' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """;

	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "RestructureApplications",
			schema: "taxonomy",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				PlanId = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false, collation: "C"),
				PlanHash = table.Column<string>(type: "text", nullable: false),
				PlanJson = table.Column<string>(type: "jsonb", nullable: false),
				ReportJson = table.Column<string>(type: "jsonb", nullable: false),
				Reason = table.Column<string>(type: "text", nullable: false),
				BaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				AppliedRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
				UndoRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
				AppliedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
				UndoneAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_RestructureApplications", x => x.Id);
				table.ForeignKey(
					name: "FK_RestructureApplications_HistoricalOperations_Id",
					column: x => x.Id,
					principalSchema: "taxonomy",
					principalTable: "HistoricalOperations",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_RestructureApplications_Revisions_AppliedRevisionId",
					column: x => x.AppliedRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_RestructureApplications_Revisions_BaseRevisionId",
					column: x => x.BaseRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_RestructureApplications_Revisions_UndoRevisionId",
					column: x => x.UndoRevisionId,
					principalSchema: "taxonomy",
					principalTable: "Revisions",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "RestructureChanges",
			schema: "taxonomy",
			columns: table => new
			{
				ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
				Ordinal = table.Column<int>(type: "integer", nullable: false),
				Kind = table.Column<string>(type: "text", maxLength: 32, nullable: false),
				SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
				Before = table.Column<Guid>(type: "uuid", nullable: true),
				After = table.Column<Guid>(type: "uuid", nullable: true),
				UndoOutcome = table.Column<string>(type: "text", maxLength: 64, nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_RestructureChanges", x => new { x.ApplicationId, x.Ordinal });
				table.ForeignKey(
					name: "FK_RestructureChanges_RestructureApplications_ApplicationId",
					column: x => x.ApplicationId,
					principalSchema: "taxonomy",
					principalTable: "RestructureApplications",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateTable(
			name: "RestructureItems",
			schema: "taxonomy",
			columns: table => new
			{
				ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				Rule = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Evidence = table.Column<string>(type: "text", nullable: false),
				PreviousOccasionId = table.Column<Guid>(type: "uuid", nullable: true),
				TargetOccasionId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_RestructureItems", x => new { x.ApplicationId, x.ItemId });
				table.ForeignKey(
					name: "FK_RestructureItems_HistoricalOperationItems_ApplicationId_Ite~",
					columns: x => new { x.ApplicationId, x.ItemId },
					principalSchema: "taxonomy",
					principalTable: "HistoricalOperationItems",
					principalColumns: new[] { "OperationId", "ItemId" },
					onDelete: ReferentialAction.Restrict);
				table.ForeignKey(
					name: "FK_RestructureItems_RestructureApplications_ApplicationId",
					column: x => x.ApplicationId,
					principalSchema: "taxonomy",
					principalTable: "RestructureApplications",
					principalColumn: "Id",
					onDelete: ReferentialAction.Restrict);
			});

		migrationBuilder.CreateIndex(
			name: "IX_RestructureApplications_AppliedRevisionId",
			schema: "taxonomy",
			table: "RestructureApplications",
			column: "AppliedRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_RestructureApplications_BaseRevisionId",
			schema: "taxonomy",
			table: "RestructureApplications",
			column: "BaseRevisionId");

		migrationBuilder.CreateIndex(
			name: "IX_RestructureApplications_PlanId",
			schema: "taxonomy",
			table: "RestructureApplications",
			column: "PlanId");

		migrationBuilder.CreateIndex(
			name: "IX_RestructureApplications_UndoRevisionId",
			schema: "taxonomy",
			table: "RestructureApplications",
			column: "UndoRevisionId");

		// Restructure applications run their purchase reassignments as their own historical operation kind.
		migrationBuilder.Sql("""
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_operation_kind_valid;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_operation_kind_valid
                    CHECK ("Kind" IN ('ManualV1', 'ApprovedMappingV1', 'JevSuggestionV1', 'RestructureV1'));
                -- Keep NOT VALID: legacy automatic captures without a header identity are retained, as before.
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_capture_mapping_identity;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_capture_mapping_identity
                    CHECK (("Kind" IN ('ManualV1', 'JevSuggestionV1', 'RestructureV1') AND "ApprovedMappingRevisionId" IS NULL)
                        OR ("Kind" = 'ApprovedMappingV1' AND "ApprovedMappingRevisionId" IS NOT NULL)) NOT VALID;
                """);
		migrationBuilder.Sql(ItemGuard);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                LOCK TABLE taxonomy."HistoricalOperations", taxonomy."HistoricalOperationItems" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations" WHERE "Kind" = 'RestructureV1') THEN
                        RAISE EXCEPTION 'Category restructure history is retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_operation_kind_valid;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_operation_kind_valid
                    CHECK ("Kind" IN ('ManualV1', 'ApprovedMappingV1', 'JevSuggestionV1'));
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_capture_mapping_identity;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_capture_mapping_identity
                    CHECK (("Kind" IN ('ManualV1', 'JevSuggestionV1') AND "ApprovedMappingRevisionId" IS NULL)
                        OR ("Kind" = 'ApprovedMappingV1' AND "ApprovedMappingRevisionId" IS NOT NULL)) NOT VALID;
                """);
		migrationBuilder.Sql(PreviousItemGuard);

		migrationBuilder.DropTable(
			name: "RestructureChanges",
			schema: "taxonomy");

		migrationBuilder.DropTable(
			name: "RestructureItems",
			schema: "taxonomy");

		migrationBuilder.DropTable(
			name: "RestructureApplications",
			schema: "taxonomy");
	}
}
