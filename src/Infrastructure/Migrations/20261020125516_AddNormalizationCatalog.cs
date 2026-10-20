using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddNormalizationCatalog : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationRevision",
			schema: "receipts",
			table: "Receipts",
			type: "uuid",
			nullable: false,
			defaultValueSql: "gen_random_uuid()");

		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationAssignmentId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "NormalizationMappingId",
			schema: "receipts",
			table: "ReceiptItems",
			type: "uuid",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "NormalizationOrigin",
			schema: "receipts",
			table: "ReceiptItems",
			type: "text",
			maxLength: 32,
			nullable: false,
			defaultValue: "unassigned");

		// Existing assignments have no reliable origin. Preserve them without inventing approval.
		migrationBuilder.Sql("""
                UPDATE receipts."ReceiptItems" SET "NormalizationOrigin" = 'legacy_unknown'
                WHERE "NormalizedDescriptionId" IS NOT NULL;
                CREATE TRIGGER receipt_normalization_revision
                BEFORE UPDATE ON receipts."Receipts"
                FOR EACH ROW EXECUTE FUNCTION matching.refresh_normalization_revision();
                CREATE FUNCTION matching.guard_catalog_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
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
                CREATE TRIGGER catalog_assignment_guard BEFORE UPDATE ON receipts."ReceiptItems"
                FOR EACH ROW EXECUTE FUNCTION matching.guard_catalog_assignment();
                CREATE FUNCTION matching.invalidate_catalog_location() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Location" IS DISTINCT FROM OLD."Location" THEN
                    UPDATE receipts."ReceiptItems"
                      SET "NormalizedDescriptionId" = NULL, "NormalizedDescriptionMatchScore" = NULL,
                          "NormalizationOrigin" = 'unassigned', "NormalizationMappingId" = NULL,
                          "NormalizationAssignmentId" = gen_random_uuid()
                      WHERE "ReceiptId" = NEW."Id" AND "NormalizationOrigin" IN ('mapped', 'excluded');
                  END IF;
                  RETURN NEW;
                END;
                $$;
                CREATE TRIGGER catalog_location_invalidation AFTER UPDATE ON receipts."Receipts"
                FOR EACH ROW EXECUTE FUNCTION matching.invalidate_catalog_location();
                """);

		migrationBuilder.AddColumn<bool>(
			name: "IncludeLegacyUnknown",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.AddColumn<string>(
			name: "SourceItemCode",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "SourceLocation",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "SourceReceiptRevision",
			schema: "matching",
			table: "NormalizationAiProposals",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "NormalizationCatalogOperations",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
				Actor = table.Column<string>(type: "text", nullable: false),
				RequestHash = table.Column<string>(type: "text", nullable: false),
				State = table.Column<string>(type: "text", nullable: false),
				ErrorCode = table.Column<string>(type: "text", nullable: true),
				ErrorDetail = table.Column<string>(type: "text", nullable: true),
				Location = table.Column<string>(type: "text", nullable: false),
				Description = table.Column<string>(type: "text", nullable: false),
				ItemCode = table.Column<string>(type: "text", nullable: true),
				TargetId = table.Column<Guid>(type: "uuid", nullable: true),
				TargetRevision = table.Column<Guid>(type: "uuid", nullable: true),
				NewCanonicalName = table.Column<string>(type: "text", nullable: true),
				MappingId = table.Column<Guid>(type: "uuid", nullable: true),
				IncludeLegacyUnknown = table.Column<bool>(type: "boolean", nullable: false),
				Exclude = table.Column<bool>(type: "boolean", nullable: false),
				CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationCatalogOperations", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "NormalizationSourceMappings",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				Revision = table.Column<Guid>(type: "uuid", nullable: false),
				SourceHash = table.Column<string>(type: "text", maxLength: 64, nullable: false),
				Location = table.Column<string>(type: "text", nullable: false),
				Description = table.Column<string>(type: "text", nullable: false),
				ItemCode = table.Column<string>(type: "text", nullable: true),
				TargetId = table.Column<Guid>(type: "uuid", nullable: true),
				State = table.Column<string>(type: "text", maxLength: 24, nullable: false),
				Exclude = table.Column<bool>(type: "boolean", nullable: false),
				ApprovedBy = table.Column<string>(type: "text", nullable: false),
				ApprovedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationSourceMappings", x => x.Id);
			});

		migrationBuilder.CreateTable(
			name: "NormalizationCatalogMembers",
			schema: "matching",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				OperationId = table.Column<Guid>(type: "uuid", nullable: false),
				ItemId = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
				SourceRevision = table.Column<Guid>(type: "uuid", nullable: false),
				ReceiptRevision = table.Column<Guid>(type: "uuid", nullable: false),
				PreviousTargetId = table.Column<Guid>(type: "uuid", nullable: true),
				PreviousOrigin = table.Column<string>(type: "text", nullable: false),
				PreviousMappingId = table.Column<Guid>(type: "uuid", nullable: true),
				PreviousAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
				PreviousScore = table.Column<double>(type: "double precision", nullable: true),
				State = table.Column<string>(type: "text", nullable: false),
				AppliedRevision = table.Column<Guid>(type: "uuid", nullable: true),
				AssignmentId = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_NormalizationCatalogMembers", x => x.Id);
				table.ForeignKey(
					name: "FK_NormalizationCatalogMembers_NormalizationCatalogOperations_~",
					column: x => x.OperationId,
					principalSchema: "matching",
					principalTable: "NormalizationCatalogOperations",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationCatalogMembers_OperationId_ItemId",
			schema: "matching",
			table: "NormalizationCatalogMembers",
			columns: new[] { "OperationId", "ItemId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationCatalogMembers_OperationId_State",
			schema: "matching",
			table: "NormalizationCatalogMembers",
			columns: new[] { "OperationId", "State" });

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationCatalogOperations_Actor_ClientRequestId",
			schema: "matching",
			table: "NormalizationCatalogOperations",
			columns: new[] { "Actor", "ClientRequestId" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "IX_NormalizationSourceMappings_SourceHash",
			schema: "matching",
			table: "NormalizationSourceMappings",
			column: "SourceHash",
			unique: true,
			filter: "\"State\" = 'active'");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DROP TRIGGER catalog_location_invalidation ON receipts."Receipts";
                DROP FUNCTION matching.invalidate_catalog_location();
                DROP TRIGGER catalog_assignment_guard ON receipts."ReceiptItems";
                DROP FUNCTION matching.guard_catalog_assignment();
                DROP TRIGGER receipt_normalization_revision ON receipts."Receipts";
                """);
		migrationBuilder.DropTable(
			name: "NormalizationCatalogMembers",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "NormalizationSourceMappings",
			schema: "matching");

		migrationBuilder.DropTable(
			name: "NormalizationCatalogOperations",
			schema: "matching");

		migrationBuilder.DropColumn(
			name: "NormalizationRevision",
			schema: "receipts",
			table: "Receipts");

		migrationBuilder.DropColumn(
			name: "NormalizationAssignmentId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationMappingId",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "NormalizationOrigin",
			schema: "receipts",
			table: "ReceiptItems");

		migrationBuilder.DropColumn(
			name: "IncludeLegacyUnknown",
			schema: "matching",
			table: "NormalizationAiProposals");

		migrationBuilder.DropColumn(
			name: "SourceItemCode",
			schema: "matching",
			table: "NormalizationAiProposals");

		migrationBuilder.DropColumn(
			name: "SourceLocation",
			schema: "matching",
			table: "NormalizationAiProposals");

		migrationBuilder.DropColumn(
			name: "SourceReceiptRevision",
			schema: "matching",
			table: "NormalizationAiProposals");
	}
}
