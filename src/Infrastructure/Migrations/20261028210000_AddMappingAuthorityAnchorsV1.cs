using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

// SQL-owned, INACTIVE structural anchors. No application writer or restore activation.
public sealed partial class AddMappingAuthorityAnchorsV1 : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE taxonomy."MappingAuthorityBaselineAnchorsV1" (
            "BaselineId" uuid PRIMARY KEY REFERENCES taxonomy."MappingAuthorityBaseline" ("Id"),
            "StreamId" uuid NOT NULL,
            "Version" integer NOT NULL CHECK ("Version" = 1),
            "CanonicalBaseline" bytea NOT NULL,
            "BaselineHash" text NOT NULL COLLATE "C" CHECK ("BaselineHash" ~ '^[0-9A-F]{64}$')
        );
        CREATE TABLE taxonomy."MappingAuthorityCheckpointsV1" (
            "BaselineId" uuid PRIMARY KEY REFERENCES taxonomy."MappingAuthorityBaselineAnchorsV1" ("BaselineId"),
            "Sequence" bigint NOT NULL CHECK ("Sequence" >= 0),
            "EventId" uuid NOT NULL CHECK ("EventId" <> '00000000-0000-0000-0000-000000000000')
        );
        CREATE OR REPLACE VIEW taxonomy."MappingAuthorityTip" AS
            SELECT a."StreamId", a."BaselineId", c."Sequence", c."EventId"
            FROM taxonomy."MappingAuthorityBaselineAnchorsV1" a
            JOIN taxonomy."MappingAuthorityCheckpointsV1" c ON c."BaselineId" = a."BaselineId";
        -- PostgreSQL C ordering compares Unicode scalars; .NET Ordinal compares UTF-16 units.
        CREATE FUNCTION taxonomy.authority_v1_ordinal(value text) RETURNS bytea
        LANGUAGE plpgsql IMMUTABLE STRICT AS $$
        DECLARE result bytea := ''::bytea; character text; codepoint integer;
        BEGIN
            FOR character IN SELECT regexp_split_to_table(value, '') LOOP
                CONTINUE WHEN character = '';
                codepoint := ascii(character);
                IF codepoint > 65535 THEN
                    codepoint := codepoint - 65536;
                    result := result || decode(lpad(to_hex(55296 + codepoint / 1024),4,'0')
                        || lpad(to_hex(56320 + codepoint % 1024),4,'0'),'hex');
                ELSE result := result || decode(lpad(to_hex(codepoint),4,'0'),'hex'); END IF;
            END LOOP;
            RETURN result;
        END $$;
        CREATE OR REPLACE FUNCTION taxonomy.snapshot_authority_baseline() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE canonical bytea; heads text;
        BEGIN
            INSERT INTO taxonomy."MappingAuthorityBaselineHeads" ("BaselineId", "MappingRevisionId")
                SELECT NEW."Id", "RevisionId" FROM taxonomy."CategoryMappingHeads";
            SELECT coalesce(string_agg('{"Source":{"Location":' || taxonomy.effect_v1_q(r."Location")
                || ',"Description":' || taxonomy.effect_v1_q(r."Description")
                || ',"ItemCode":' || taxonomy.effect_v1_q(r."ItemCode")
                || '},"RevisionId":' || taxonomy.effect_v1_q(r."Id"::text) || '}', ','
                ORDER BY taxonomy.authority_v1_ordinal(r."Location"),
                    taxonomy.authority_v1_ordinal(r."Description"),
                    taxonomy.authority_v1_ordinal(r."ItemCode") NULLS FIRST), '') INTO heads
                FROM taxonomy."MappingAuthorityBaselineHeads" h
                JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = h."MappingRevisionId"
                WHERE h."BaselineId" = NEW."Id";
            canonical := convert_to('{"Version":1,"StreamId":' || taxonomy.effect_v1_q(NEW."StreamId"::text)
                || ',"Id":' || taxonomy.effect_v1_q(NEW."Id"::text) || ',"Heads":[' || heads || ']}','UTF8');
            INSERT INTO taxonomy."MappingAuthorityBaselineAnchorsV1"
                ("BaselineId","StreamId","Version","CanonicalBaseline","BaselineHash")
                VALUES (NEW."Id",NEW."StreamId",1,canonical,
                    upper(encode(sha256(convert_to('receipts.mapping-authority.baseline.v1','UTF8')
                        || decode('00','hex') || canonical),'hex')));
            INSERT INTO taxonomy."MappingAuthorityCheckpointsV1" VALUES (NEW."Id",0,NEW."Id");
            RETURN NEW;
        END $$;
        -- Never backfill anchors from pre-existing evidence: its original completeness is unknown.
        CREATE FUNCTION taxonomy.guard_authority_anchor_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF pg_trigger_depth() <> 2 THEN
                RAISE EXCEPTION 'Authority anchors are maintained only by baseline and append boundaries' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER guard_authority_anchor_v1 BEFORE INSERT ON taxonomy."MappingAuthorityBaselineAnchorsV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_authority_anchor_v1();
        CREATE TRIGGER immutable_authority_anchor_v1 BEFORE UPDATE OR DELETE ON taxonomy."MappingAuthorityBaselineAnchorsV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE TRIGGER retain_authority_anchor_v1 BEFORE TRUNCATE ON taxonomy."MappingAuthorityBaselineAnchorsV1"
            FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE TRIGGER guard_authority_checkpoint_v1 BEFORE INSERT OR UPDATE ON taxonomy."MappingAuthorityCheckpointsV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_authority_anchor_v1();
        CREATE TRIGGER retain_authority_checkpoint_delete_v1 BEFORE DELETE ON taxonomy."MappingAuthorityCheckpointsV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE TRIGGER retain_authority_checkpoint_truncate_v1 BEFORE TRUNCATE ON taxonomy."MappingAuthorityCheckpointsV1"
            FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE FUNCTION taxonomy.validate_authority_checkpoint_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE checkpoint taxonomy."MappingAuthorityCheckpointsV1"%ROWTYPE; tail uuid; sequence bigint;
        BEGIN
            -- One event per SQL statement. AFTER-row checkpoint updates run at statement end;
            -- a second BEFORE row sees the first inserted event and rejects the whole statement.
            -- Multiple separate append statements in the same transaction remain supported.
            IF current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'Live mapping authority writes require READ COMMITTED isolation' USING ERRCODE = '25000';
            END IF;
            PERFORM 1 FROM taxonomy."Head" WHERE "Id" = 1 FOR UPDATE;
            SELECT * INTO checkpoint FROM taxonomy."MappingAuthorityCheckpointsV1" WHERE "BaselineId" = NEW."BaselineId";
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Missing independently retained authority anchor; legacy evidence cannot be resealed' USING ERRCODE = '23514';
            END IF;
            SELECT "Id", "Sequence" INTO tail, sequence FROM taxonomy."MappingAuthorityEvents" ORDER BY "Sequence" DESC LIMIT 1;
            IF coalesce(sequence,0) <> checkpoint."Sequence" OR coalesce(tail,NEW."BaselineId") <> checkpoint."EventId"
                OR (SELECT count(*) FROM taxonomy."MappingAuthorityEvents") <> checkpoint."Sequence" THEN
                RAISE EXCEPTION 'Authority evidence does not match retained checkpoint' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;
        -- Trigger names order the independent check before the existing append allocator.
        CREATE TRIGGER anchor_authority_append_v1 BEFORE INSERT ON taxonomy."MappingAuthorityEvents"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.validate_authority_checkpoint_v1();
        CREATE FUNCTION taxonomy.advance_authority_checkpoint_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE taxonomy."MappingAuthorityCheckpointsV1" SET "Sequence" = NEW."Sequence", "EventId" = NEW."Id"
                WHERE "BaselineId" = NEW."BaselineId" AND "Sequence" = NEW."Sequence" - 1 AND "EventId" = NEW."PreviousEventId";
            IF NOT FOUND THEN RAISE EXCEPTION 'Authority checkpoint predecessor mismatch' USING ERRCODE = '23514'; END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER advance_authority_checkpoint_v1 AFTER INSERT ON taxonomy."MappingAuthorityEvents"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.advance_authority_checkpoint_v1();
        """);

	protected override void Down(MigrationBuilder migrationBuilder) =>
		throw new NotSupportedException("Authority anchors are retained; roll forward instead.");
}
