using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalApprovedMappingProvenance : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "Kind",
			schema: "taxonomy",
			table: "HistoricalOperations",
			type: "text",
			nullable: false,
			defaultValue: "ManualV1");

		migrationBuilder.AddColumn<Guid>(
			name: "ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperationItems_ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "ApprovedMappingRevisionId");

		migrationBuilder.AddForeignKey(
			name: "FK_HistoricalOperationItems_CategoryMappingRevisions_ApprovedM~",
			schema: "taxonomy",
			table: "HistoricalOperationItems",
			column: "ApprovedMappingRevisionId",
			principalSchema: "taxonomy",
			principalTable: "CategoryMappingRevisions",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
		// Structural evidence only. A future automatic writer must recheck the current mapping,
		// source and taxonomy semantics under the classification write locks.
		migrationBuilder.Sql("""
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_operation_kind_valid
                    CHECK ("Kind" IN ('ManualV1', 'ApprovedMappingV1'));
                CREATE FUNCTION taxonomy.guard_historical_provenance_header() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Kind" IS DISTINCT FROM OLD."Kind" THEN
                        RAISE EXCEPTION 'Historical operation kind is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER historical_provenance_header BEFORE UPDATE ON taxonomy."HistoricalOperations"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_historical_provenance_header();

                CREATE FUNCTION taxonomy.guard_historical_provenance_item() RETURNS trigger LANGUAGE plpgsql AS $$
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
                CREATE TRIGGER historical_provenance_item BEFORE INSERT OR UPDATE ON taxonomy."HistoricalOperationItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_historical_provenance_item();
                """);

	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                LOCK TABLE taxonomy."HistoricalOperations", taxonomy."HistoricalOperationItems" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations" WHERE "Kind" <> 'ManualV1') THEN
                        RAISE EXCEPTION 'Automatic historical provenance is retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                DROP TRIGGER historical_provenance_item ON taxonomy."HistoricalOperationItems";
                DROP FUNCTION taxonomy.guard_historical_provenance_item();
                DROP TRIGGER historical_provenance_header ON taxonomy."HistoricalOperations";
                DROP FUNCTION taxonomy.guard_historical_provenance_header();
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_operation_kind_valid;
                """);

		migrationBuilder.DropForeignKey(
			name: "FK_HistoricalOperationItems_CategoryMappingRevisions_ApprovedM~",
			schema: "taxonomy",
			table: "HistoricalOperationItems");

		migrationBuilder.DropIndex(
			name: "IX_HistoricalOperationItems_ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperationItems");

		migrationBuilder.DropColumn(
			name: "Kind",
			schema: "taxonomy",
			table: "HistoricalOperations");

		migrationBuilder.DropColumn(
			name: "ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperationItems");
	}
}
