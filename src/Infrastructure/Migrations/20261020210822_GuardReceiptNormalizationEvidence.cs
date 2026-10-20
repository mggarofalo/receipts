using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class GuardReceiptNormalizationEvidence : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "SourceHash",
			schema: "matching",
			table: "NormalizationReviewWork",
			type: "text",
			maxLength: 64,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "text",
			oldMaxLength: 64);
		migrationBuilder.Sql("""
                CREATE FUNCTION matching.review_item_prepared(item_id uuid) RETURNS boolean
                LANGUAGE sql STABLE AS $$
                  SELECT position(',' || item_id::text || ',' in coalesce(current_setting('receipts.review_prepared', true), '')) > 0;
                $$;

                CREATE FUNCTION matching.guard_receipt_review_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NOT matching.review_item_prepared(NEW."Id") AND (
                    TG_OP = 'INSERT' OR NEW."Description" IS DISTINCT FROM OLD."Description"
                    OR NEW."ReceiptItemCode" IS DISTINCT FROM OLD."ReceiptItemCode"
                    OR NEW."ReceiptId" IS DISTINCT FROM OLD."ReceiptId"
                    OR NEW."NormalizedDescriptionId" IS DISTINCT FROM OLD."NormalizedDescriptionId"
                    OR NEW."NormalizationAssignmentId" IS DISTINCT FROM OLD."NormalizationAssignmentId"
                    OR NEW."NormalizationMappingId" IS DISTINCT FROM OLD."NormalizationMappingId"
                    OR NEW."NormalizationReviewReason" IS DISTINCT FROM OLD."NormalizationReviewReason"
                    OR NEW."ReviewedBy" IS DISTINCT FROM OLD."ReviewedBy"
                    OR NEW."ReviewedAt" IS DISTINCT FROM OLD."ReviewedAt"
                    OR NEW."DeletedAt" IS DISTINCT FROM OLD."DeletedAt"
                    OR NEW."NormalizationReviewState" IS DISTINCT FROM OLD."NormalizationReviewState"
                    OR NEW."NormalizationReviewDecisionId" IS DISTINCT FROM OLD."NormalizationReviewDecisionId"
                    OR NEW."ReviewedTargetId" IS DISTINCT FROM OLD."ReviewedTargetId"
                    OR NEW."ReviewedSourceHash" IS DISTINCT FROM OLD."ReviewedSourceHash"
                    OR NEW."ReviewMappingRevision" IS DISTINCT FROM OLD."ReviewMappingRevision") THEN
                    NEW."NormalizationReviewState" := 'NeedsReview';
                    NEW."NormalizationReviewReason" := CASE WHEN NEW."DeletedAt" IS NULL THEN 'source_changed' ELSE 'unavailable' END;
                    NEW."NormalizationReviewDecisionId" := NULL;
                    NEW."ReviewedTargetId" := NULL;
                    NEW."ReviewedSourceHash" := NULL;
                    NEW."ReviewMappingRevision" := NULL;
                    NEW."NormalizationSourceHash" := NULL;
                  END IF;
                  RETURN NEW;
                END;
                $$;
                CREATE TRIGGER review_evidence_guard BEFORE INSERT OR UPDATE ON receipts."ReceiptItems"
                  FOR EACH ROW EXECUTE FUNCTION matching.guard_receipt_review_evidence();

                CREATE FUNCTION matching.observe_receipt_review_work() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."NormalizationReviewState" = 'NeedsReview' AND NEW."DeletedAt" IS NULL
                    AND EXISTS (SELECT 1 FROM receipts."Receipts" r WHERE r."Id" = NEW."ReceiptId" AND r."DeletedAt" IS NULL) THEN
                    INSERT INTO matching."NormalizationReviewWork" ("ItemId", "ReceiptId", "SourceHash", "SourceRevision", "CreatedAt", "UpdatedAt")
                    VALUES (NEW."Id", NEW."ReceiptId", NEW."NormalizationSourceHash", NEW."NormalizationRevision", now(), now())
                    ON CONFLICT ("ItemId") DO UPDATE SET "ReceiptId" = EXCLUDED."ReceiptId", "SourceHash" = EXCLUDED."SourceHash",
                      "SourceRevision" = EXCLUDED."SourceRevision", "UpdatedAt" = now(),
                      "CreatedAt" = CASE WHEN matching."NormalizationReviewWork"."SourceHash" IS NOT DISTINCT FROM EXCLUDED."SourceHash"
                        THEN matching."NormalizationReviewWork"."CreatedAt" ELSE now() END;
                  ELSE
                    DELETE FROM matching."NormalizationReviewWork" WHERE "ItemId" = NEW."Id";
                  END IF;
                  RETURN NEW;
                END;
                $$;
                CREATE TRIGGER review_work_observation AFTER INSERT OR UPDATE ON receipts."ReceiptItems"
                  FOR EACH ROW EXECUTE FUNCTION matching.observe_receipt_review_work();

                CREATE OR REPLACE FUNCTION matching.invalidate_catalog_location() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Location" IS DISTINCT FROM OLD."Location" OR NEW."DeletedAt" IS DISTINCT FROM OLD."DeletedAt" THEN
                    UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'NeedsReview',
                      "NormalizationReviewReason" = CASE WHEN NEW."DeletedAt" IS NULL THEN 'source_changed' ELSE 'unavailable' END,
                      "NormalizationReviewDecisionId" = NULL, "ReviewedSourceHash" = NULL, "ReviewedTargetId" = NULL,
                      "ReviewMappingRevision" = NULL, "NormalizationSourceHash" = NULL
                    WHERE "ReceiptId" = NEW."Id" AND NOT matching.review_item_prepared("Id");
                  END IF;
                  RETURN NEW;
                END;
                $$;

                CREATE FUNCTION matching.invalidate_mapping_review_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE authority_changed boolean;
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'NeedsReview',
                      "NormalizationReviewReason" = 'mapping_changed', "NormalizationReviewDecisionId" = NULL,
                      "ReviewedSourceHash" = NULL, "ReviewedTargetId" = NULL, "ReviewMappingRevision" = NULL
                    WHERE "NormalizationMappingId" = OLD."Id" AND "NormalizationReviewReason" = 'approved_mapping';
                    RETURN OLD;
                  END IF;
                  authority_changed := NEW."SourceHash" IS DISTINCT FROM OLD."SourceHash"
                    OR NEW."Location" IS DISTINCT FROM OLD."Location" OR NEW."Description" IS DISTINCT FROM OLD."Description"
                    OR NEW."ItemCode" IS DISTINCT FROM OLD."ItemCode" OR NEW."Exclude" IS DISTINCT FROM OLD."Exclude"
                    OR NEW."ExclusionReason" IS DISTINCT FROM OLD."ExclusionReason"
                    OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedAt" IS DISTINCT FROM OLD."ApprovedAt";
                  IF authority_changed OR NEW."State" IS DISTINCT FROM OLD."State" OR NEW."Revision" IS DISTINCT FROM OLD."Revision"
                    OR NEW."TargetId" IS DISTINCT FROM OLD."TargetId" THEN
                    UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'NeedsReview',
                      "NormalizationReviewReason" = 'mapping_changed', "NormalizationReviewDecisionId" = NULL,
                      "ReviewedSourceHash" = NULL, "ReviewedTargetId" = NULL, "ReviewMappingRevision" = NULL
                    WHERE "NormalizationMappingId" = NEW."Id" AND "NormalizationReviewReason" = 'approved_mapping'
                      AND (authority_changed OR NEW."State" <> 'active' OR "ReviewMappingRevision" IS DISTINCT FROM NEW."Revision"
                        OR "ReviewedTargetId" IS DISTINCT FROM NEW."TargetId");
                  END IF;
                  RETURN NEW;
                END;
                $$;
                CREATE TRIGGER mapping_review_invalidation AFTER UPDATE OR DELETE ON matching."NormalizationSourceMappings"
                  FOR EACH ROW EXECUTE FUNCTION matching.invalidate_mapping_review_evidence();

                CREATE FUNCTION matching.invalidate_target_review_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."Status" <> 'Active' AND NEW."Status" IS DISTINCT FROM OLD."Status" THEN
                    UPDATE receipts."ReceiptItems" SET "NormalizationReviewState" = 'NeedsReview',
                      "NormalizationReviewReason" = 'target_changed', "NormalizationReviewDecisionId" = NULL,
                      "ReviewedSourceHash" = NULL, "ReviewedTargetId" = NULL, "ReviewMappingRevision" = NULL
                    WHERE "NormalizedDescriptionId" = NEW."Id" AND "NormalizationReviewState" = 'Verified';
                  END IF;
                  RETURN NEW;
                END;
                $$;
                CREATE TRIGGER target_review_invalidation AFTER UPDATE ON matching."NormalizedDescriptions"
                  FOR EACH ROW EXECUTE FUNCTION matching.invalidate_target_review_evidence();
                """);

	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
                DROP TRIGGER target_review_invalidation ON matching."NormalizedDescriptions";
                DROP FUNCTION matching.invalidate_target_review_evidence();
                DROP TRIGGER mapping_review_invalidation ON matching."NormalizationSourceMappings";
                DROP FUNCTION matching.invalidate_mapping_review_evidence();
                DROP TRIGGER review_work_observation ON receipts."ReceiptItems";
                DROP FUNCTION matching.observe_receipt_review_work();
                DROP TRIGGER review_evidence_guard ON receipts."ReceiptItems";
                DROP FUNCTION matching.guard_receipt_review_evidence();
                CREATE OR REPLACE FUNCTION matching.invalidate_catalog_location() RETURNS trigger LANGUAGE plpgsql AS $$
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
                DROP FUNCTION matching.review_item_prepared(uuid);
                UPDATE matching."NormalizationReviewWork" SET "SourceHash" = '' WHERE "SourceHash" IS NULL;
                """);
		migrationBuilder.AlterColumn<string>(
			name: "SourceHash",
			schema: "matching",
			table: "NormalizationReviewWork",
			type: "text",
			maxLength: 64,
			nullable: false,
			defaultValue: "",
			oldClrType: typeof(string),
			oldType: "text",
			oldMaxLength: 64,
			oldNullable: true);
	}
}
