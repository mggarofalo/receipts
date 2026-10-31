using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropNormalizedDescriptionMatchScore : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		// plpgsql resolves columns at run time: drop the retired score from the catalog guard first,
		// or every edit of a mapped item would fail once the column is gone.
		migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION matching.guard_catalog_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."Description" IS DISTINCT FROM OLD."Description"
                      OR NEW."ReceiptItemCode" IS DISTINCT FROM OLD."ReceiptItemCode")
                      AND OLD."NormalizationOrigin" IN ('mapped', 'excluded')
                      AND NEW."NormalizationAssignmentId" IS NOT DISTINCT FROM OLD."NormalizationAssignmentId" THEN
                    NEW."NormalizedDescriptionId" := NULL;
                    NEW."NormalizationOrigin" := 'unassigned';
                    NEW."NormalizationMappingId" := NULL;
                    NEW."NormalizationAssignmentId" := gen_random_uuid();
                  ELSIF NEW."NormalizedDescriptionId" IS DISTINCT FROM OLD."NormalizedDescriptionId"
                      AND NEW."NormalizationAssignmentId" IS NOT DISTINCT FROM OLD."NormalizationAssignmentId" THEN
                    NEW."NormalizationOrigin" := CASE WHEN NEW."NormalizedDescriptionId" IS NULL THEN 'unassigned' ELSE 'manual' END;
                    NEW."NormalizationMappingId" := NULL;
                    NEW."NormalizationAssignmentId" := gen_random_uuid();
                  END IF;
                  RETURN NEW;
                END;
                $$;
                """);

		migrationBuilder.DropColumn(
			name: "NormalizedDescriptionMatchScore",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "PreviousScore",
			schema: "matching",
			table: "NormalizationCatalogMembers");

		migrationBuilder.DropColumn(
			name: "SourceMatchScore",
			schema: "matching",
			table: "NormalizationAiProposals");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<double>(
			name: "NormalizedDescriptionMatchScore",
			schema: "receipts",
			table: "ReceiptItems",
			type: "double precision",
			nullable: true);

		migrationBuilder.AddColumn<double>(
			name: "PreviousScore",
			schema: "matching",
			table: "NormalizationCatalogMembers",
			type: "double precision",
			nullable: true);

		migrationBuilder.AddColumn<double>(
			name: "SourceMatchScore",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "double precision",
			nullable: true);

		migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION matching.guard_catalog_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."Description" IS DISTINCT FROM OLD."Description"
                      OR NEW."ReceiptItemCode" IS DISTINCT FROM OLD."ReceiptItemCode")
                      AND OLD."NormalizationOrigin" IN ('mapped', 'excluded')
                      AND NEW."NormalizationAssignmentId" IS NOT DISTINCT FROM OLD."NormalizationAssignmentId" THEN
                    NEW."NormalizedDescriptionId" := NULL;
                    NEW."NormalizedDescriptionMatchScore" := NULL;
                    NEW."NormalizationOrigin" := 'unassigned';
                    NEW."NormalizationMappingId" := NULL;
                    NEW."NormalizationAssignmentId" := gen_random_uuid();
                  ELSIF NEW."NormalizedDescriptionId" IS DISTINCT FROM OLD."NormalizedDescriptionId"
                      AND NEW."NormalizationAssignmentId" IS NOT DISTINCT FROM OLD."NormalizationAssignmentId" THEN
                    NEW."NormalizationOrigin" := CASE WHEN NEW."NormalizedDescriptionId" IS NULL THEN 'unassigned' ELSE 'manual' END;
                    NEW."NormalizationMappingId" := NULL;
                    NEW."NormalizationAssignmentId" := gen_random_uuid();
                  END IF;
                  RETURN NEW;
                END;
                $$;
                """);
	}
}
