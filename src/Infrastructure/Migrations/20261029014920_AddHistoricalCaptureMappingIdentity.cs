using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddHistoricalCaptureMappingIdentity : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "IX_HistoricalOperations_ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations",
			column: "ApprovedMappingRevisionId");

		migrationBuilder.AddForeignKey(
			name: "FK_HistoricalOperations_CategoryMappingRevisions_ApprovedMappi~",
			schema: "taxonomy",
			table: "HistoricalOperations",
			column: "ApprovedMappingRevisionId",
			principalSchema: "taxonomy",
			principalTable: "CategoryMappingRevisions",
			principalColumn: "Id",
			onDelete: ReferentialAction.Restrict);
		// Legacy automatic captures lack a header identity. Retain them but do not
		// infer authority from their item rows. The check blocks header updates; runtime paths reject them.
		migrationBuilder.Sql("""
                ALTER TABLE taxonomy."HistoricalOperations" ADD CONSTRAINT historical_capture_mapping_identity
                    CHECK (("Kind" = 'ManualV1' AND "ApprovedMappingRevisionId" IS NULL)
                        OR ("Kind" = 'ApprovedMappingV1' AND "ApprovedMappingRevisionId" IS NOT NULL)) NOT VALID;
                CREATE OR REPLACE FUNCTION taxonomy.guard_historical_provenance_header() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Kind" IS DISTINCT FROM OLD."Kind" THEN
                        RAISE EXCEPTION 'Historical operation kind is immutable' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."ApprovedMappingRevisionId" IS DISTINCT FROM OLD."ApprovedMappingRevisionId" THEN
                        RAISE EXCEPTION 'Historical capture mapping is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE FUNCTION taxonomy.guard_historical_capture_mapping() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations" h
                        WHERE h."Id" = NEW."OperationId" AND h."ApprovedMappingRevisionId" IS NOT NULL
                          AND h."ApprovedMappingRevisionId" IS DISTINCT FROM NEW."ApprovedMappingRevisionId") THEN
                        RAISE EXCEPTION 'Historical item mapping differs from capture' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER historical_capture_mapping BEFORE INSERT OR UPDATE ON taxonomy."HistoricalOperationItems"
                    FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_historical_capture_mapping();
                """);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                LOCK TABLE taxonomy."HistoricalOperations" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM taxonomy."HistoricalOperations" WHERE "ApprovedMappingRevisionId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Historical capture mapping evidence is retained' USING ERRCODE = '23514';
                    END IF;
                END $$;
                DROP TRIGGER historical_capture_mapping ON taxonomy."HistoricalOperationItems";
                DROP FUNCTION taxonomy.guard_historical_capture_mapping();
                ALTER TABLE taxonomy."HistoricalOperations" DROP CONSTRAINT historical_capture_mapping_identity;
                CREATE OR REPLACE FUNCTION taxonomy.guard_historical_provenance_header() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Kind" IS DISTINCT FROM OLD."Kind" THEN
                        RAISE EXCEPTION 'Historical operation kind is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
		migrationBuilder.DropForeignKey(
			name: "FK_HistoricalOperations_CategoryMappingRevisions_ApprovedMappi~",
			schema: "taxonomy",
			table: "HistoricalOperations");

		migrationBuilder.DropIndex(
			name: "IX_HistoricalOperations_ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations");

		migrationBuilder.DropColumn(
			name: "ApprovedMappingRevisionId",
			schema: "taxonomy",
			table: "HistoricalOperations");
	}
}
