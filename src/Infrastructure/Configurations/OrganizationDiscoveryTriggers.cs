using Microsoft.EntityFrameworkCore.Migrations;
namespace Infrastructure.Migrations;

public partial class AddOrganizationDiscovery
{
	private static void AddDiscoveryTriggers(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
            -- Bounded hash keys avoid PostgreSQL's btree limit for long descriptions.
            -- These indexes only filter candidates; discovery rechecks exact strings/nulls.
            CREATE INDEX "IX_Organization_ItemRawSource" ON receipts."ReceiptItems"
                ((md5("Description" || chr(31) || coalesce("ReceiptItemCode", ''))), "Id");
            CREATE INDEX "IX_Organization_ItemNormalizedSource" ON receipts."ReceiptItems"
                ((md5(btrim("Description") || chr(31) || coalesce(nullif(btrim("ReceiptItemCode"), ''), ''))), "Id");

            CREATE FUNCTION organization.mark_item_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE item_id uuid;
            BEGIN
                IF TG_TABLE_SCHEMA = 'taxonomy' THEN
                    IF TG_OP = 'DELETE' THEN item_id := OLD."ReceiptItemId"; ELSE item_id := NEW."ReceiptItemId"; END IF;
                ELSE
                    IF TG_OP = 'DELETE' THEN item_id := OLD."Id"; ELSE item_id := NEW."Id"; END IF;
                END IF;
                INSERT INTO organization."DirtyItems" ("ItemId") VALUES (item_id)
                ON CONFLICT ("ItemId") DO UPDATE SET "ItemId" = EXCLUDED."ItemId";
                RETURN NULL;
            END $$;
            CREATE TRIGGER organization_item_dirty AFTER INSERT OR UPDATE OR DELETE ON receipts."ReceiptItems"
                FOR EACH ROW EXECUTE FUNCTION organization.mark_item_dirty();
            CREATE TRIGGER organization_assignment_dirty AFTER INSERT OR UPDATE OR DELETE ON taxonomy."CurrentAssignments"
                FOR EACH ROW EXECUTE FUNCTION organization.mark_item_dirty();

            CREATE FUNCTION organization.mark_receipt_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE receipt_id uuid;
            BEGIN
                IF TG_OP = 'UPDATE' AND OLD."Location" IS NOT DISTINCT FROM NEW."Location"
                    AND OLD."NormalizationRevision" IS NOT DISTINCT FROM NEW."NormalizationRevision"
                    AND OLD."DeletedAt" IS NOT DISTINCT FROM NEW."DeletedAt" THEN
                    RETURN NULL;
                END IF;
                IF TG_OP = 'DELETE' THEN receipt_id := OLD."Id"; ELSE receipt_id := NEW."Id"; END IF;
                INSERT INTO organization."DirtyItems" ("ItemId")
                    SELECT "Id" FROM receipts."ReceiptItems" WHERE "ReceiptId" = receipt_id ORDER BY "Id"
                    ON CONFLICT ("ItemId") DO UPDATE SET "ItemId" = EXCLUDED."ItemId";
                RETURN NULL;
            END $$;
            CREATE TRIGGER organization_receipt_dirty AFTER UPDATE OR DELETE ON receipts."Receipts"
                FOR EACH ROW EXECUTE FUNCTION organization.mark_receipt_dirty();

            CREATE FUNCTION organization.enqueue_source(source_kind text, source_store text, source_description text, source_code text)
            RETURNS void LANGUAGE plpgsql AS $$
            DECLARE source_id uuid;
            BEGIN
                -- Hash is only a bounded index key; exact tuple equality guards every collision.
                source_id := md5(jsonb_build_array(source_kind, source_store, source_description, source_code)::text)::uuid;
                INSERT INTO organization."DirtySources" ("Id", "FormatVersion", "Kind", "Store", "Description", "ItemCode", "Cursor")
                VALUES (source_id, 1, source_kind, source_store, source_description, source_code, NULL)
                ON CONFLICT ("Id") DO UPDATE SET "Cursor" = NULL
                WHERE "DirtySources"."Kind" = EXCLUDED."Kind" AND "DirtySources"."Store" COLLATE "C" = EXCLUDED."Store"
                  AND "DirtySources"."Description" COLLATE "C" = EXCLUDED."Description"
                  AND "DirtySources"."ItemCode" COLLATE "C" IS NOT DISTINCT FROM EXCLUDED."ItemCode";
                IF NOT FOUND THEN RAISE EXCEPTION 'organization_source_hash_collision'; END IF;
            END $$;
            CREATE FUNCTION organization.mark_disposition_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    PERFORM organization.enqueue_source('raw', OLD."Store", OLD."RawDescription", OLD."ItemCode");
                END IF;
                IF TG_OP <> 'DELETE' THEN
                    PERFORM organization.enqueue_source('raw', NEW."Store", NEW."RawDescription", NEW."ItemCode");
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER organization_disposition_dirty AFTER INSERT OR UPDATE OR DELETE ON taxonomy."HistoricalGroupDispositions"
                FOR EACH ROW EXECUTE FUNCTION organization.mark_disposition_dirty();

            CREATE FUNCTION organization.enqueue_preference_source(attempt_id uuid) RETURNS void LANGUAGE plpgsql AS $$
            DECLARE attempt record;
            BEGIN
                SELECT "Location", "Description", "ItemCodeKey" INTO attempt FROM category_assistance."Attempts" WHERE "Id" = attempt_id;
                IF FOUND THEN
                    PERFORM organization.enqueue_source('normalized', attempt."Location", attempt."Description", nullif(attempt."ItemCodeKey", ''));
                END IF;
            END $$;
            CREATE FUNCTION organization.mark_preference_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN PERFORM organization.enqueue_preference_source(OLD."AttemptId"); END IF;
                IF TG_OP <> 'DELETE' THEN PERFORM organization.enqueue_preference_source(NEW."AttemptId"); END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER organization_preference_dirty AFTER INSERT OR UPDATE OR DELETE ON category_assistance."ReviewPreferences"
                FOR EACH ROW EXECUTE FUNCTION organization.mark_preference_dirty();
            """);
	}

	private static void RemoveDiscoveryTriggers(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
            DROP INDEX receipts."IX_Organization_ItemRawSource";
            DROP INDEX receipts."IX_Organization_ItemNormalizedSource";
            DROP TRIGGER organization_preference_dirty ON category_assistance."ReviewPreferences";
            DROP TRIGGER organization_disposition_dirty ON taxonomy."HistoricalGroupDispositions";
            DROP TRIGGER organization_assignment_dirty ON taxonomy."CurrentAssignments";
            DROP TRIGGER organization_receipt_dirty ON receipts."Receipts";
            DROP TRIGGER organization_item_dirty ON receipts."ReceiptItems";
            DROP FUNCTION organization.mark_preference_dirty();
            DROP FUNCTION organization.enqueue_preference_source(uuid);
            DROP FUNCTION organization.enqueue_source(text, text, text, text);
            DROP FUNCTION organization.mark_disposition_dirty();
            DROP FUNCTION organization.mark_receipt_dirty();
            DROP FUNCTION organization.mark_item_dirty();
            """);
	}
}
