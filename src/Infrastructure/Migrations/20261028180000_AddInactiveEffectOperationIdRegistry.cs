using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

// SQL-owned foundation only. Production writers and portable restore do not participate.
public sealed partial class AddInactiveEffectOperationIdRegistry : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- SHARE blocks INSERT/UPDATE/DELETE until the migration transaction commits.
        -- Stop application writers/restores first: their existing lock order is not this order.
        LOCK TABLE taxonomy."Revisions", taxonomy."Operations", taxonomy."CategoryMappingRevisions",
            taxonomy."Assignments", taxonomy."AssignmentOperations", taxonomy."HistoricalOperations",
            taxonomy."HistoricalOperationItems", taxonomy."HistoricalDispositionOperations",
            taxonomy."HistoricalGroupDispositions",
            taxonomy."LegacyMigrations", taxonomy."LegacyCrosswalks", taxonomy."LegacyItemOutcomes",
            category_assistance."HistoricalRequests", category_assistance."ReviewOperations",
            category_assistance."Attempts", matching."NormalizationCatalogOperations",
            matching."NormalizationCatalogMembers", matching."NormalizationAiRuns",
            matching."NormalizationAssistanceAttempts", receipts."ReceiptWriteRequests",
            taxonomy."OrganizationEffectOperations" IN SHARE MODE;

        CREATE TABLE taxonomy."EffectOperationIdRegistry" (
            "OperationId" uuid PRIMARY KEY,
            "Class" text NOT NULL COLLATE "C",
            "ManifestId" uuid,
            "ItemId" uuid,
            "Phase" text COLLATE "C",
            CONSTRAINT "CK_EffectOperationIdRegistry_Class" CHECK (
                ("Class" = 'ordinary' AND "ManifestId" IS NULL AND "ItemId" IS NULL AND "Phase" IS NULL)
                OR ("Class" IN ('effect','legacy_blocked') AND "ManifestId" IS NOT NULL
                    AND "ItemId" IS NOT NULL AND "Phase" IS NOT NULL AND "Phase" IN ('apply','undo')))
        );
        CREATE UNIQUE INDEX "IX_EffectOperationIdRegistry_OwnerPhase"
            ON taxonomy."EffectOperationIdRegistry" ("ManifestId","ItemId","Phase")
            WHERE "Class" IN ('effect','legacy_blocked');
        -- Multiple ordinary domains, actors and related rows deliberately collapse to one class.
        INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class")
        SELECT DISTINCT id, 'ordinary' FROM (
            SELECT "OperationId" AS id FROM taxonomy."Revisions"
            UNION ALL SELECT "OperationId" FROM taxonomy."Operations"
            UNION ALL SELECT "OperationId" FROM taxonomy."CategoryMappingRevisions"
            UNION ALL SELECT "OperationId" FROM taxonomy."Assignments"
            UNION ALL SELECT "OperationId" FROM taxonomy."AssignmentOperations"
            UNION ALL SELECT "Id" FROM taxonomy."HistoricalOperations"
            UNION ALL SELECT "OperationId" FROM taxonomy."HistoricalOperationItems"
            UNION ALL SELECT "ApplyOperationId" FROM taxonomy."HistoricalOperationItems"
            UNION ALL SELECT "UndoOperationId" FROM taxonomy."HistoricalOperationItems"
            UNION ALL SELECT "OperationId" FROM taxonomy."HistoricalDispositionOperations"
            UNION ALL SELECT "OperationId" FROM taxonomy."HistoricalGroupDispositions"
            UNION ALL SELECT "OperationId" FROM taxonomy."LegacyMigrations"
            UNION ALL SELECT "OperationId" FROM taxonomy."LegacyCrosswalks"
            UNION ALL SELECT "OperationId" FROM taxonomy."LegacyItemOutcomes"
            UNION ALL SELECT "OperationId" FROM category_assistance."HistoricalRequests"
            UNION ALL SELECT "OperationId" FROM category_assistance."ReviewOperations"
            UNION ALL SELECT "DecisionOperationId" FROM category_assistance."Attempts"
            UNION ALL SELECT "RequestId" FROM category_assistance."Attempts"
            UNION ALL SELECT "Id" FROM matching."NormalizationCatalogOperations"
            UNION ALL SELECT "ClientRequestId" FROM matching."NormalizationCatalogOperations"
            UNION ALL SELECT "OperationId" FROM matching."NormalizationCatalogMembers"
            UNION ALL SELECT "ClientRequestId" FROM matching."NormalizationAiRuns"
            UNION ALL SELECT "ClientRequestId" FROM matching."NormalizationAssistanceAttempts"
            UNION ALL SELECT "RequestId" FROM receipts."ReceiptWriteRequests"
        ) ordinary WHERE id IS NOT NULL;

        -- Preserve ambiguous history without asserting that an existing assignment was an effect.
        -- This is the sole pre-immutability conversion and never changes any source history.
        INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class","ManifestId","ItemId","Phase")
        SELECT "OperationId",'effect',"ManifestId","ItemId","Phase"
        FROM taxonomy."OrganizationEffectOperations"
        ON CONFLICT ("OperationId") DO UPDATE SET "Class" = 'legacy_blocked',
            "ManifestId" = EXCLUDED."ManifestId", "ItemId" = EXCLUDED."ItemId", "Phase" = EXCLUDED."Phase";

        CREATE TRIGGER immutable_effect_operation_id_registry BEFORE UPDATE OR DELETE
            ON taxonomy."EffectOperationIdRegistry" FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE TRIGGER retain_effect_operation_id_registry BEFORE TRUNCATE
            ON taxonomy."EffectOperationIdRegistry" FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();

        CREATE FUNCTION taxonomy.claim_ordinary_operation_id(id uuid) RETURNS void LANGUAGE plpgsql AS $$
        BEGIN
            IF id IS NULL THEN RAISE EXCEPTION 'Operation identity is required' USING ERRCODE = '22023'; END IF;
            INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class")
                VALUES (id,'ordinary') ON CONFLICT ("OperationId") DO NOTHING;
            IF NOT EXISTS (SELECT 1 FROM taxonomy."EffectOperationIdRegistry" WHERE "OperationId" = id AND "Class" = 'ordinary') THEN
                RAISE EXCEPTION 'Operation identity is reserved or legacy blocked' USING ERRCODE = '23505';
            END IF;
        END $$;

        CREATE FUNCTION taxonomy.claim_effect_operation_ids(manifest uuid, item uuid, apply_id uuid, undo_id uuid)
        RETURNS void LANGUAGE plpgsql AS $$
        BEGIN
            IF manifest IS NULL OR item IS NULL OR apply_id IS NULL OR undo_id IS NULL
                OR '00000000-0000-0000-0000-000000000000'::uuid IN (manifest,item,apply_id,undo_id)
                OR apply_id = undo_id THEN
                RAISE EXCEPTION 'Distinct nonempty phase identities and owner are required' USING ERRCODE = '22023';
            END IF;
            -- Unique-index arbitration waits for uncommitted claims. Stable ordering avoids
            -- opposite phase order deadlocks for a single pair. Both claims roll back on failure.
            INSERT INTO taxonomy."EffectOperationIdRegistry" ("OperationId","Class","ManifestId","ItemId","Phase")
                SELECT id,'effect',manifest,item,phase FROM (VALUES (apply_id,'apply'),(undo_id,'undo')) v(id,phase)
                ORDER BY id ON CONFLICT ("OperationId") DO NOTHING;
            IF (SELECT count(*) FROM taxonomy."EffectOperationIdRegistry"
                WHERE "Class" = 'effect' AND "ManifestId" = manifest AND "ItemId" = item
                AND (("OperationId" = apply_id AND "Phase" = 'apply') OR ("OperationId" = undo_id AND "Phase" = 'undo'))) <> 2 THEN
                RAISE EXCEPTION 'Effect identity is occupied by another class or owner' USING ERRCODE = '23505';
            END IF;
        END $$;
        """);

	protected override void Down(MigrationBuilder migrationBuilder) =>
		throw new NotSupportedException("Operation identity history is retained; roll forward instead.");
}
