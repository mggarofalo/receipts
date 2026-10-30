using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddJevAutomaticWrites : Migration
{
	private const string ItemGuard = """
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

	private const string PreviousItemGuard = """
                CREATE OR REPLACE FUNCTION taxonomy.guard_historical_provenance_item() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE operation_kind text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND NEW."ApprovedMappingRevisionId" IS DISTINCT FROM OLD."ApprovedMappingRevisionId" THEN
                        RAISE EXCEPTION 'Historical provenance is immutable' USING ERRCODE = '23514';
                    END IF;
                    SELECT "Kind" INTO operation_kind FROM taxonomy."HistoricalOperations" WHERE "Id" = NEW."OperationId";
                    IF operation_kind = 'ManualV1' AND NEW."ApprovedMappingRevisionId" IS NOT NULL THEN
                        RAISE EXCEPTION 'Manual historical item cannot carry mapping provenance' USING ERRCODE = '23514';
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
		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.AddColumn<bool>(
			name: "AutoApply",
			schema: "category_assistance",
			table: "Settings",
			type: "boolean",
			nullable: false,
			defaultValue: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "AutoWriteCheckedAt",
			schema: "category_assistance",
			table: "Attempts",
			type: "timestamptz",
			nullable: true);

		// Each stage's billed usage is recorded, whatever the outcome; first-stage usage already had its columns.
		migrationBuilder.AddColumn<long>(
			name: "SecondStageInputTokens",
			schema: "category_assistance",
			table: "Attempts",
			type: "bigint",
			nullable: true);

		migrationBuilder.AddColumn<long>(
			name: "SecondStageOutputTokens",
			schema: "category_assistance",
			table: "Attempts",
			type: "bigint",
			nullable: true);

		migrationBuilder.UpdateData(
			schema: "category_assistance",
			table: "Settings",
			keyColumn: "Id",
			keyValue: 1,
			column: "AutoApply",
			value: true);

		// A strong match that was written automatically leaves the review queue and the pending limit.
		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts",
			sql: "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate','accepted','dismissed','applied')");

		// Automatic Jev writes reuse the automatic activity and undo surface as their own operation kind.
		migrationBuilder.Sql("""
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_operation_kind_valid;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_operation_kind_valid
                    CHECK ("Kind" IN ('ManualV1', 'ApprovedMappingV1', 'JevSuggestionV1'));
                -- Keep NOT VALID: legacy automatic captures without a header identity are retained, as before.
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_capture_mapping_identity;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_capture_mapping_identity
                    CHECK (("Kind" IN ('ManualV1', 'JevSuggestionV1') AND "ApprovedMappingRevisionId" IS NULL)
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
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations" WHERE "Kind" = 'JevSuggestionV1')
                        OR EXISTS (SELECT 1 FROM category_assistance."Attempts" WHERE "State" = 'applied') THEN
                        RAISE EXCEPTION 'Automatic Jev history is retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_operation_kind_valid;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_operation_kind_valid
                    CHECK ("Kind" IN ('ManualV1', 'ApprovedMappingV1'));
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_capture_mapping_identity;
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_capture_mapping_identity
                    CHECK (("Kind" = 'ManualV1' AND "ApprovedMappingRevisionId" IS NULL)
                        OR ("Kind" = 'ApprovedMappingV1' AND "ApprovedMappingRevisionId" IS NOT NULL)) NOT VALID;
                """);
		migrationBuilder.Sql(PreviousItemGuard);

		migrationBuilder.DropCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "AutoApply",
			schema: "category_assistance",
			table: "Settings");

		migrationBuilder.DropColumn(
			name: "AutoWriteCheckedAt",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "SecondStageInputTokens",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.DropColumn(
			name: "SecondStageOutputTokens",
			schema: "category_assistance",
			table: "Attempts");

		migrationBuilder.AddCheckConstraint(
			name: "CK_CategoryAssistance_AttemptState",
			schema: "category_assistance",
			table: "Attempts",
			sql: "\"State\" IN ('queued','dispatching','suggested','abstained','failed','stale','indeterminate','accepted','dismissed')");
	}
}
