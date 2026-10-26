using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

public sealed partial class AddPortableSourceRevisionRestore : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
            CREATE TABLE matching."SourceRevisionRestores" (
                "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                "SourceTable" text NOT NULL CHECK ("SourceTable" IN ('Receipts','ReceiptItems')),
                "SourceId" uuid NOT NULL,
                "PreviousRevision" uuid NOT NULL,
                "RestoredRevision" uuid NOT NULL,
                "Transaction" xid8 NOT NULL DEFAULT pg_current_xact_id(),
                "RecordedAt" timestamptz NOT NULL DEFAULT now(),
                "Consumed" boolean NOT NULL DEFAULT false
            );
            CREATE INDEX "IX_SourceRevisionRestores_Fence" ON matching."SourceRevisionRestores" ("Transaction", "SourceTable", "SourceId") WHERE NOT "Consumed";
            CREATE FUNCTION matching.guard_source_restore_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'UPDATE' AND pg_trigger_depth() > 1
                    AND NOT OLD."Consumed" AND NEW."Consumed"
                    AND (to_jsonb(OLD) - 'Consumed') = (to_jsonb(NEW) - 'Consumed') THEN RETURN NEW; END IF;
                RAISE EXCEPTION 'Source restore evidence is immutable';
            END; $$;
            CREATE TRIGGER immutable_source_restore BEFORE UPDATE OR DELETE ON matching."SourceRevisionRestores"
                FOR EACH ROW EXECUTE FUNCTION matching.guard_source_restore_evidence();
            CREATE TRIGGER immutable_source_restore_truncate BEFORE TRUNCATE ON matching."SourceRevisionRestores"
                FOR EACH STATEMENT EXECUTE FUNCTION matching.guard_source_restore_evidence();
            CREATE FUNCTION matching.restore_source_revision(source_table text, source_id uuid, expected uuid, restored uuid)
            RETURNS void LANGUAGE plpgsql AS $$
            DECLARE affected integer;
            BEGIN
                IF source_table NOT IN ('Receipts','ReceiptItems') OR restored = '00000000-0000-0000-0000-000000000000'::uuid THEN
                    RAISE EXCEPTION 'Invalid source revision restore';
                END IF;
                -- A restore may only run under the importer's exclusive table fence.
                IF NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = pg_backend_pid()
                    AND relation = format('receipts.%I', source_table)::regclass
                    AND mode = 'ExclusiveLock' AND granted) THEN
                    RAISE EXCEPTION 'Source restore requires exclusive table lock';
                END IF;
                IF expected = restored THEN
                    EXECUTE format('SELECT 1 FROM receipts.%I WHERE "Id" = $1 AND "NormalizationRevision" = $2 FOR UPDATE', source_table)
                        USING source_id, expected;
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    IF affected <> 1 THEN RAISE EXCEPTION 'Source restore compare-and-swap failed'; END IF;
                    RETURN;
                END IF;
                INSERT INTO matching."SourceRevisionRestores" ("SourceTable", "SourceId", "PreviousRevision", "RestoredRevision")
                    VALUES (source_table, source_id, expected, restored);
                EXECUTE format('UPDATE receipts.%I SET "NormalizationRevision" = $1 WHERE "Id" = $2 AND "NormalizationRevision" = $3', source_table)
                    USING restored, source_id, expected;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected <> 1 THEN RAISE EXCEPTION 'Source restore compare-and-swap failed'; END IF;
            END;
            $$;
            CREATE OR REPLACE FUNCTION matching.refresh_normalization_revision() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE restore_id uuid;
            BEGIN
                IF NEW."NormalizationRevision" IS DISTINCT FROM OLD."NormalizationRevision"
                    AND (to_jsonb(NEW) - 'NormalizationRevision') = (to_jsonb(OLD) - 'NormalizationRevision') THEN
                    SELECT "Id" INTO restore_id FROM matching."SourceRevisionRestores"
                    WHERE "SourceTable" = TG_TABLE_NAME AND "SourceId" = NEW."Id"
                        AND "PreviousRevision" = OLD."NormalizationRevision" AND "RestoredRevision" = NEW."NormalizationRevision"
                        AND "Transaction" = pg_current_xact_id() AND NOT "Consumed"
                    LIMIT 1 FOR UPDATE;
                    IF restore_id IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = pg_backend_pid()
                            AND relation = TG_RELID AND mode = 'ExclusiveLock' AND granted) THEN
                            RAISE EXCEPTION 'Source restore requires exclusive table lock';
                        END IF;
                        UPDATE matching."SourceRevisionRestores" SET "Consumed" = true WHERE "Id" = restore_id;
                        RETURN NEW;
                    END IF;
                END IF;
                NEW."NormalizationRevision" := gen_random_uuid();
                RETURN NEW;
            END;
            $$;
            """);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
            LOCK TABLE matching."SourceRevisionRestores" IN ACCESS EXCLUSIVE MODE;
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM matching."SourceRevisionRestores") THEN
                    RAISE EXCEPTION 'Cannot remove retained source restore evidence';
                END IF;
            END $$;
            CREATE OR REPLACE FUNCTION matching.refresh_normalization_revision() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN NEW."NormalizationRevision" := gen_random_uuid(); RETURN NEW; END; $$;
            DROP FUNCTION matching.restore_source_revision(text,uuid,uuid,uuid);
            DROP TABLE matching."SourceRevisionRestores";
            DROP FUNCTION matching.guard_source_restore_evidence();
            """);
	}
}
