using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

// SQL-owned evidence: deliberately absent from the mutable EF application model.
public sealed partial class AddMappingAuthorityJournalStorage : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""
            CREATE TABLE taxonomy."MappingAuthorityBaseline" (
                "Singleton" integer PRIMARY KEY CHECK ("Singleton" = 1),
                "Id" uuid NOT NULL UNIQUE CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'),
                "StreamId" uuid NOT NULL UNIQUE CHECK ("StreamId" <> '00000000-0000-0000-0000-000000000000'),
                "Version" integer NOT NULL DEFAULT 1 CHECK ("Version" = 1)
            );
            CREATE TABLE taxonomy."MappingAuthorityBaselineHeads" (
                "MappingRevisionId" uuid PRIMARY KEY REFERENCES taxonomy."CategoryMappingRevisions" ("Id"),
                "BaselineId" uuid NOT NULL REFERENCES taxonomy."MappingAuthorityBaseline" ("Id")
            );
            CREATE TABLE taxonomy."MappingAuthorityEvents" (
                "Id" uuid PRIMARY KEY CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'),
                "BaselineId" uuid NOT NULL REFERENCES taxonomy."MappingAuthorityBaseline" ("Id"),
                "Version" integer NOT NULL DEFAULT 1 CHECK ("Version" = 1),
                "Sequence" bigint NOT NULL UNIQUE CHECK ("Sequence" > 0),
                "PreviousEventId" uuid NOT NULL,
                "Kind" integer NOT NULL CHECK ("Kind" IN (1, 2)),
                "MappingRevisionId" uuid NOT NULL REFERENCES taxonomy."CategoryMappingRevisions" ("Id"),
                "CaptureId" uuid UNIQUE,
                CHECK (("Kind" = 1 AND "CaptureId" IS NULL) OR
                    ("Kind" = 2 AND "CaptureId" IS NOT NULL AND "CaptureId" <> '00000000-0000-0000-0000-000000000000'))
            );
            CREATE UNIQUE INDEX "IX_MappingAuthorityTransitionRevision" ON taxonomy."MappingAuthorityEvents" ("MappingRevisionId") WHERE "Kind" = 1;
            CREATE VIEW taxonomy."MappingAuthorityTip" AS
                SELECT b."StreamId", b."Id" AS "BaselineId", COALESCE(e."Sequence", 0) AS "Sequence",
                    COALESCE(e."Id", b."Id") AS "EventId"
                FROM taxonomy."MappingAuthorityBaseline" b
                LEFT JOIN LATERAL (SELECT "Sequence", "Id" FROM taxonomy."MappingAuthorityEvents"
                    ORDER BY "Sequence" DESC LIMIT 1) e ON true;

            CREATE FUNCTION taxonomy.guard_authority_baseline() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                -- A lock alone does not refresh an older REPEATABLE READ/SERIALIZABLE snapshot.
                -- Live snapshot/capture callers must use statement-fresh READ COMMITTED reads.
                IF current_setting('transaction_isolation') <> 'read committed' THEN
                    RAISE EXCEPTION 'Live mapping authority writes require READ COMMITTED isolation' USING ERRCODE = '25000';
                END IF;
                PERFORM 1 FROM taxonomy."Head" WHERE "Id" = 1 FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Taxonomy head missing' USING ERRCODE = '23514'; END IF;
                IF EXISTS (SELECT 1 FROM taxonomy."CategoryMappingHeads" h
                    JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = h."RevisionId"
                    WHERE h."SourceFingerprint" <> r."SourceFingerprint") THEN
                    RAISE EXCEPTION 'Mapping head source mismatch' USING ERRCODE = '23514';
                END IF;
                IF EXISTS (SELECT 1 FROM taxonomy."CategoryMappingHeads" h
                    JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = h."RevisionId"
                    GROUP BY r."Location" COLLATE "C", r."Description" COLLATE "C", r."ItemCode" COLLATE "C"
                    HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'Duplicate exact mapping source' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER guard_authority_baseline BEFORE INSERT ON taxonomy."MappingAuthorityBaseline"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_authority_baseline();
            CREATE FUNCTION taxonomy.snapshot_authority_baseline() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                INSERT INTO taxonomy."MappingAuthorityBaselineHeads" ("BaselineId", "MappingRevisionId")
                    SELECT NEW."Id", "RevisionId" FROM taxonomy."CategoryMappingHeads";
                RETURN NEW;
            END $$;
            CREATE TRIGGER snapshot_authority_baseline AFTER INSERT ON taxonomy."MappingAuthorityBaseline"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.snapshot_authority_baseline();
            CREATE FUNCTION taxonomy.guard_authority_baseline_head() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF pg_trigger_depth() <> 2 THEN
                    RAISE EXCEPTION 'Baseline heads are generated only by the baseline snapshot' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER guard_authority_baseline_head BEFORE INSERT ON taxonomy."MappingAuthorityBaselineHeads"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_authority_baseline_head();

            CREATE FUNCTION taxonomy.append_authority_event() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE baseline uuid; tail_id uuid; tail_sequence bigint; current_id uuid;
                revision taxonomy."CategoryMappingRevisions"%ROWTYPE;
            BEGIN
                -- All current mapping producers use this same transaction lock.
                -- A lock alone does not refresh an older REPEATABLE READ/SERIALIZABLE snapshot.
                -- Live snapshot/capture callers must use statement-fresh READ COMMITTED reads.
                IF current_setting('transaction_isolation') <> 'read committed' THEN
                    RAISE EXCEPTION 'Live mapping authority writes require READ COMMITTED isolation' USING ERRCODE = '25000';
                END IF;
                PERFORM 1 FROM taxonomy."Head" WHERE "Id" = 1 FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Taxonomy head missing' USING ERRCODE = '23514'; END IF;
                SELECT "BaselineId", "Sequence", "EventId" INTO baseline, tail_sequence, tail_id FROM taxonomy."MappingAuthorityTip";
                IF baseline IS NULL OR NEW."BaselineId" IS DISTINCT FROM baseline OR NEW."Id" = baseline THEN
                    RAISE EXCEPTION 'Invalid authority baseline' USING ERRCODE = '23514';
                END IF;
                IF tail_sequence = 9223372036854775807 THEN
                    RAISE EXCEPTION 'Authority sequence exhausted' USING ERRCODE = '23514';
                END IF;
                IF (NEW."Sequence" IS NOT NULL AND NEW."Sequence" <> tail_sequence + 1)
                    OR (NEW."PreviousEventId" IS NOT NULL AND NEW."PreviousEventId" <> tail_id) THEN
                    RAISE EXCEPTION 'Authority gap or fork' USING ERRCODE = '23514';
                END IF;
                NEW."Sequence" := tail_sequence + 1;
                NEW."PreviousEventId" := tail_id;
                SELECT * INTO revision FROM taxonomy."CategoryMappingRevisions" WHERE "Id" = NEW."MappingRevisionId";
                IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM taxonomy."CategoryMappingHeads" h
                    WHERE h."SourceFingerprint" = revision."SourceFingerprint" AND h."RevisionId" = revision."Id") THEN
                    RAISE EXCEPTION 'Authority event requires the actual mapping head' USING ERRCODE = '23514';
                END IF;
                SELECT r."Id" INTO current_id FROM taxonomy."MappingAuthorityEvents" e
                    JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = e."MappingRevisionId"
                    WHERE e."Kind" = 1 AND r."Location" COLLATE "C" = revision."Location" COLLATE "C"
                    AND r."Description" COLLATE "C" = revision."Description" COLLATE "C"
                    AND r."ItemCode" COLLATE "C" IS NOT DISTINCT FROM revision."ItemCode" COLLATE "C"
                    ORDER BY e."Sequence" DESC LIMIT 1;
                IF current_id IS NULL THEN
                    SELECT r."Id" INTO current_id FROM taxonomy."MappingAuthorityBaselineHeads" h
                        JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = h."MappingRevisionId"
                        WHERE r."Location" COLLATE "C" = revision."Location" COLLATE "C"
                        AND r."Description" COLLATE "C" = revision."Description" COLLATE "C"
                        AND r."ItemCode" COLLATE "C" IS NOT DISTINCT FROM revision."ItemCode" COLLATE "C";
                END IF;
                IF NEW."Kind" = 1 THEN
                    IF revision."PreviousRevisionId" IS DISTINCT FROM current_id
                        OR (current_id IS NULL AND revision."CategoryId" IS NULL)
                        OR EXISTS (SELECT 1 FROM taxonomy."MappingAuthorityBaselineHeads" WHERE "MappingRevisionId" = revision."Id") THEN
                        RAISE EXCEPTION 'Authority transition predecessor mismatch' USING ERRCODE = '23514';
                    END IF;
                ELSIF NEW."Kind" = 2 THEN
                    IF current_id IS DISTINCT FROM revision."Id" OR revision."CategoryId" IS NULL THEN
                        RAISE EXCEPTION 'Authority capture head mismatch' USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER append_authority_event BEFORE INSERT ON taxonomy."MappingAuthorityEvents"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.append_authority_event();
            CREATE TRIGGER immutable_authority_baseline BEFORE UPDATE OR DELETE ON taxonomy."MappingAuthorityBaseline"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER immutable_authority_baseline_heads BEFORE UPDATE OR DELETE ON taxonomy."MappingAuthorityBaselineHeads"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER immutable_authority_events BEFORE UPDATE OR DELETE ON taxonomy."MappingAuthorityEvents"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER retain_authority_baseline BEFORE TRUNCATE ON taxonomy."MappingAuthorityBaseline"
                FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER retain_authority_baseline_heads BEFORE TRUNCATE ON taxonomy."MappingAuthorityBaselineHeads"
                FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER retain_authority_events BEFORE TRUNCATE ON taxonomy."MappingAuthorityEvents"
                FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
            """);
	}

	protected override void Down(MigrationBuilder migrationBuilder) =>
		throw new NotSupportedException("Mapping authority evidence is retained; roll forward instead.");
}
