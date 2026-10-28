using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

// Inactive SQL-owned capture binding; no application writer or authority activation.
public sealed partial class AddOrganizationEffectCaptureWitnessV1 : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql("""

        CREATE OR REPLACE FUNCTION taxonomy.effect_v1_string(value text) RETURNS bytea
        LANGUAGE plpgsql IMMUTABLE STRICT AS $body$
        DECLARE
            result text := '"';
            character text;
            codepoint integer;
            unit integer;
        BEGIN
            IF current_setting('server_encoding') <> 'UTF8' THEN
                RAISE EXCEPTION 'effect v1 prototype requires UTF8' USING ERRCODE = '22023';
            END IF;
            FOR character IN SELECT regexp_split_to_table(value, '') LOOP
                CONTINUE WHEN character = '';
                codepoint := ascii(character);
                result := result || CASE codepoint
                    WHEN 8 THEN chr(92) || 'b'
                    WHEN 9 THEN chr(92) || 't'
                    WHEN 10 THEN chr(92) || 'n'
                    WHEN 12 THEN chr(92) || 'f'
                    WHEN 13 THEN chr(92) || 'r'
                    WHEN 92 THEN chr(92) || chr(92)
                    ELSE CASE
                        WHEN codepoint BETWEEN 32 AND 126 AND codepoint NOT IN (34, 38, 39, 43, 60, 62, 96)
                            THEN character
                        WHEN codepoint <= 65535
                            THEN chr(92) || 'u' || upper(lpad(to_hex(codepoint), 4, '0'))
                        ELSE '' END
                    END;
                IF codepoint > 65535 THEN
                    unit := codepoint - 65536;
                    result := result || chr(92) || 'u' || upper(to_hex(55296 + (unit / 1024)))
                                     || chr(92) || 'u' || upper(to_hex(56320 + (unit % 1024)));
                END IF;
            END LOOP;
            RETURN convert_to(result || '"', 'UTF8');
        END;
        $body$;
        
        CREATE FUNCTION taxonomy.effect_v1_q(value text) RETURNS text LANGUAGE SQL IMMUTABLE AS $$
            SELECT coalesce(convert_from(taxonomy.effect_v1_string(value), 'UTF8'), 'null')
        $$;
        CREATE FUNCTION taxonomy.effect_v1_obj(VARIADIC pairs text[]) RETURNS text LANGUAGE SQL IMMUTABLE AS $$
            SELECT '{' || string_agg(taxonomy.effect_v1_q(pairs[i]) || ':' || pairs[i+1], ',' ORDER BY pairs[i] COLLATE "C") || '}'
            FROM generate_series(1, cardinality(pairs), 2) i
        $$;
        

        -- char.IsWhiteSpace's complete BMP set; verified against the running .NET codec.
        -- Supplementary scalars are never whitespace in .NET and survive translate.
        CREATE FUNCTION taxonomy.effect_v1_metadata_text(value text) RETURNS boolean LANGUAGE SQL IMMUTABLE STRICT AS $$
            SELECT translate(value, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000', '') <> ''
        $$;
        CREATE FUNCTION taxonomy.effect_v1_relational(manifest uuid, ids uuid[]) RETURNS bytea LANGUAGE plpgsql AS $$
        DECLARE h taxonomy."OrganizationEffectManifests"%ROWTYPE;
            i taxonomy."OrganizationEffectItems"%ROWTYPE;
            mapping taxonomy."CategoryMappingRevisions"%ROWTYPE;
            node taxonomy."Nodes"%ROWTYPE;
            members text[] := ARRAY[]::text[]; canonical bytea; d jsonb; selected uuid; path uuid[]; n integer := 0; zero uuid := '00000000-0000-0000-0000-000000000000';
        BEGIN
            SELECT * INTO h FROM taxonomy."OrganizationEffectManifests" WHERE "Id" = manifest;
            IF NOT FOUND OR ids IS NULL OR cardinality(ids) < 1 OR array_ndims(ids) <> 1
                OR cardinality(ids) <> (SELECT count(DISTINCT id) FROM unnest(ids) id)
                OR EXISTS (SELECT unnest(ids) EXCEPT SELECT "ItemId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest)
                OR EXISTS (SELECT "ItemId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest EXCEPT SELECT unnest(ids))
            THEN RAISE EXCEPTION 'invalid complete ordered item vector' USING ERRCODE = '22023'; END IF;
            IF h."Id" = zero OR h."TaxonomyRevisionId" = zero OR NOT taxonomy.effect_v1_metadata_text(h."Actor") OR NOT taxonomy.effect_v1_metadata_text(h."PolicyVersion")
                OR NOT isfinite(h."CapturedAt") OR extract(year FROM h."CapturedAt" AT TIME ZONE 'UTC') NOT BETWEEN 1 AND 9999
            THEN RAISE EXCEPTION 'invalid manifest context' USING ERRCODE = '22023'; END IF;
            IF (SELECT count(DISTINCT (btrim("Store"), btrim("RawDescription"), nullif(btrim("ItemCode"),'')))
                FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest) <> 1
            THEN RAISE EXCEPTION 'multiple source groups' USING ERRCODE = '22023'; END IF;

            FOREACH selected IN ARRAY ids LOOP
                SELECT * INTO STRICT i FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest AND "ItemId" = selected;
                d := i."DecisionEvidenceJson";
                IF jsonb_typeof(d) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(d)) <> 5
                    OR NOT d ?& ARRAY['Version','Kind','MappingRevisionId','ApprovalTaxonomyRevisionId','SourceFingerprint']
                    OR d->'Version' IS DISTINCT FROM '1'::jsonb OR d->>'Kind' IS DISTINCT FROM 'approved-exact-mapping'
                    OR jsonb_typeof(d->'MappingRevisionId') <> 'string' OR jsonb_typeof(d->'ApprovalTaxonomyRevisionId') <> 'string'
                    OR jsonb_typeof(d->'SourceFingerprint') <> 'string' OR d->>'SourceFingerprint' !~ '^[0-9A-F]{64}$'
                THEN RAISE EXCEPTION 'invalid decision contract' USING ERRCODE = '22023'; END IF;
                SELECT * INTO mapping FROM taxonomy."CategoryMappingRevisions" WHERE "Id" = (d->>'MappingRevisionId')::uuid;
                IF NOT FOUND OR mapping."Id" = zero OR mapping."CategoryId" IS DISTINCT FROM i."TargetCategoryId"
                    OR mapping."TaxonomyRevisionId" <> (d->>'ApprovalTaxonomyRevisionId')::uuid
                    OR mapping."SourceFingerprint" <> d->>'SourceFingerprint'
                    OR (mapping."Location",mapping."Description",mapping."ItemCode") IS DISTINCT FROM
                        (btrim(i."Store"),btrim(i."RawDescription"),nullif(btrim(i."ItemCode"),''))
                THEN RAISE EXCEPTION 'mapping decision context differs' USING ERRCODE = '22023'; END IF;
                IF i."TaxonomyRevisionId" <> h."TaxonomyRevisionId" OR zero IN (i."ItemId",i."ReceiptId",i."ItemRevision",i."ReceiptRevision",i."ApplyOperationId",i."UndoOperationId")
                    OR i."ExpectedAssignmentId" = zero OR i."ApplyOperationId" = i."UndoOperationId"
                    OR NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectOperations" o WHERE o."OperationId" = i."ApplyOperationId" AND o."ManifestId" = manifest AND o."ItemId" = selected AND o."Phase" = 'apply')
                    OR NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectOperations" o WHERE o."OperationId" = i."UndoOperationId" AND o."ManifestId" = manifest AND o."ItemId" = selected AND o."Phase" = 'undo')
                THEN RAISE EXCEPTION 'invalid item context or operations' USING ERRCODE = '22023'; END IF;
                path := ARRAY[]::uuid[]; selected := i."TargetCategoryId";
                WHILE selected IS NOT NULL LOOP
                    IF selected = zero OR selected = ANY(path) THEN RAISE EXCEPTION 'invalid ancestry' USING ERRCODE = '22023'; END IF;
                    SELECT * INTO node FROM taxonomy."Nodes" WHERE "RevisionId" = h."TaxonomyRevisionId" AND "CategoryId" = selected;
                    IF NOT FOUND OR node."Archived" THEN RAISE EXCEPTION 'missing or archived ancestor' USING ERRCODE = '22023'; END IF;
                    path := array_append(path,selected); selected := node."ParentId";
                END LOOP;
                members := array_append(members, taxonomy.effect_v1_obj(
                    'Version','1','ManifestId',taxonomy.effect_v1_q(h."Id"::text),
                    'Actor',taxonomy.effect_v1_q(h."Actor"),'PolicyVersion',taxonomy.effect_v1_q(h."PolicyVersion"),
                    'CapturedAt',taxonomy.effect_v1_q(to_char(h."CapturedAt" AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
                    'Source',taxonomy.effect_v1_obj('ReceiptId',taxonomy.effect_v1_q(i."ReceiptId"::text),
                        'ItemId',taxonomy.effect_v1_q(i."ItemId"::text),'Store',taxonomy.effect_v1_q(i."Store"),
                        'RawDescription',taxonomy.effect_v1_q(i."RawDescription"),'ItemCode',taxonomy.effect_v1_q(i."ItemCode"),
                        'ReceiptRevision',taxonomy.effect_v1_q(i."ReceiptRevision"::text),'ItemRevision',taxonomy.effect_v1_q(i."ItemRevision"::text)),
                    'ExpectedAssignmentId',taxonomy.effect_v1_q(i."ExpectedAssignmentId"::text),
                    'Target',taxonomy.effect_v1_obj('CategoryId',taxonomy.effect_v1_q(i."TargetCategoryId"::text),
                        'TaxonomyRevisionId',taxonomy.effect_v1_q(h."TaxonomyRevisionId"::text),
                        'Path',(SELECT '[' || string_agg(taxonomy.effect_v1_q(p::text),',' ORDER BY ord) || ']' FROM unnest(path) WITH ORDINALITY x(p,ord))),
                    'ApplyOperationId',taxonomy.effect_v1_q(i."ApplyOperationId"::text),'UndoOperationId',taxonomy.effect_v1_q(i."UndoOperationId"::text),
                    'Decision',taxonomy.effect_v1_obj('Version','1','Kind',taxonomy.effect_v1_q('approved-exact-mapping'),
                        'MappingRevisionId',taxonomy.effect_v1_q(mapping."Id"::text),'ApprovalTaxonomyRevisionId',taxonomy.effect_v1_q(mapping."TaxonomyRevisionId"::text),
                        'SourceFingerprint',taxonomy.effect_v1_q(mapping."SourceFingerprint"))));
                n := n + 1;
            END LOOP;
            canonical := convert_to(taxonomy.effect_v1_obj('Version','1','ManifestId',taxonomy.effect_v1_q(h."Id"::text),
                'Actor',taxonomy.effect_v1_q(h."Actor"),'PolicyVersion',taxonomy.effect_v1_q(h."PolicyVersion"),
                'TaxonomyRevisionId',taxonomy.effect_v1_q(h."TaxonomyRevisionId"::text),
                'CapturedAt',taxonomy.effect_v1_q(to_char(h."CapturedAt" AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
                'Members','[' || array_to_string(members,',') || ']'),'UTF8');
            IF octet_length(canonical) > 1048576 THEN RAISE EXCEPTION 'manifest request exceeds v1 limit' USING ERRCODE = '22023'; END IF;
            RETURN canonical;
        EXCEPTION WHEN invalid_text_representation THEN
            RAISE EXCEPTION 'invalid decision identity' USING ERRCODE = '22023';
        END $$;
        

            -- Explicit opt-in discriminants leave historical opaque captures unchanged.
            ALTER TABLE taxonomy."OrganizationEffectManifests" ADD COLUMN "CaptureWitnessVersion" integer
                CHECK ("CaptureWitnessVersion" = 1);
            ALTER TABLE taxonomy."MappingAuthorityEvents" ADD COLUMN "CaptureWitnessVersion" integer
                CHECK ("CaptureWitnessVersion" = 1 AND "Kind" = 2);
            ALTER TABLE taxonomy."MappingAuthorityEvents" ADD COLUMN "WriteTransaction" bigint;
            CREATE FUNCTION taxonomy.stamp_authority_event_transaction() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN NEW."WriteTransaction" := txid_current(); RETURN NEW; END $$;
            CREATE TRIGGER stamp_authority_event_transaction BEFORE INSERT ON taxonomy."MappingAuthorityEvents"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.stamp_authority_event_transaction();

            CREATE FUNCTION taxonomy.acquire_capture_gate_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."CaptureWitnessVersion" = 1 THEN
                    IF current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'Typed capture requires READ COMMITTED' USING ERRCODE = '25000';
                    END IF;
                    -- Future producers must acquire RCPT/NORM at transaction entry before any
                    -- baseline/event/mapping/taxonomy work that takes Head. This trigger cannot
                    -- repair lock order if a caller already holds Head from an untyped action.
                    PERFORM pg_advisory_xact_lock(1380143188,1313821261);
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER acquire_capture_gate_v1 BEFORE INSERT ON taxonomy."OrganizationEffectManifests"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.acquire_capture_gate_v1();
            CREATE TRIGGER acquire_capture_gate_v1 BEFORE INSERT ON taxonomy."MappingAuthorityEvents"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.acquire_capture_gate_v1();

            CREATE TABLE taxonomy."OrganizationEffectCaptureWitnessesV1" (
                "ManifestId" uuid PRIMARY KEY REFERENCES taxonomy."OrganizationEffectManifests" ("Id"),
                "EventId" uuid NOT NULL UNIQUE REFERENCES taxonomy."MappingAuthorityEvents" ("Id"),
                "OrderedItemIds" uuid[] NOT NULL,
                "CanonicalRequest" bytea NOT NULL,
                "RequestHash" text NOT NULL COLLATE "C"
            );
            CREATE FUNCTION taxonomy.seal_effect_capture_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE manifest taxonomy."OrganizationEffectManifests"%ROWTYPE;
                event taxonomy."MappingAuthorityEvents"%ROWTYPE;
            BEGIN
                IF current_setting('transaction_isolation') <> 'read committed' THEN
                    RAISE EXCEPTION 'Capture seal requires READ COMMITTED' USING ERRCODE = '25000';
                END IF;
                PERFORM 1 FROM taxonomy."Head" WHERE "Id" = 1 FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Taxonomy head missing' USING ERRCODE = '23514'; END IF;
                SELECT * INTO manifest FROM taxonomy."OrganizationEffectManifests" WHERE "Id" = NEW."ManifestId";
                SELECT * INTO event FROM taxonomy."MappingAuthorityEvents" WHERE "Id" = NEW."EventId";
                IF manifest."Id" IS NULL OR event."Id" IS NULL OR manifest."CaptureWitnessVersion" IS DISTINCT FROM 1
                    OR event."CaptureWitnessVersion" IS DISTINCT FROM 1 OR event."Kind" <> 2
                    OR event."CaptureId" IS DISTINCT FROM manifest."Id"
                    OR manifest."WriteTransaction" IS DISTINCT FROM txid_current()
                    OR event."WriteTransaction" IS DISTINCT FROM txid_current()
                    OR manifest."TaxonomyRevisionId" IS DISTINCT FROM (SELECT "RevisionId" FROM taxonomy."Head" WHERE "Id" = 1)
                    OR NOT EXISTS (SELECT 1 FROM taxonomy."CategoryMappingHeads" h
                        JOIN taxonomy."CategoryMappingRevisions" r ON r."Id" = h."RevisionId"
                        WHERE r."Id" = event."MappingRevisionId" AND h."SourceFingerprint" = r."SourceFingerprint" AND r."CategoryId" IS NOT NULL)
                THEN RAISE EXCEPTION 'Invalid live typed capture context' USING ERRCODE = '23514'; END IF;
                -- Every member must use the journal event's single mapping, not merely the same source.
                IF EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectItems" i WHERE i."ManifestId" = NEW."ManifestId"
                    AND i."DecisionEvidenceJson"->>'MappingRevisionId' IS DISTINCT FROM event."MappingRevisionId"::text)
                THEN RAISE EXCEPTION 'Capture mapping differs from event' USING ERRCODE = '23514'; END IF;
                PERFORM r."Id" FROM receipts."Receipts" r
                    WHERE r."Id" IN (SELECT "ReceiptId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = NEW."ManifestId")
                    ORDER BY r."Id" FOR UPDATE;
                PERFORM p."Id" FROM receipts."ReceiptItems" p
                    WHERE p."Id" IN (SELECT "ItemId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = NEW."ManifestId")
                    ORDER BY p."Id" FOR UPDATE;
                IF EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectItems" i
                    LEFT JOIN receipts."ReceiptItems" p ON p."Id" = i."ItemId"
                    LEFT JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
                    LEFT JOIN taxonomy."CurrentAssignments" c ON c."ReceiptItemId" = i."ItemId"
                    LEFT JOIN taxonomy."Assignments" a ON a."Id" = c."AssignmentId"
                    WHERE i."ManifestId" = NEW."ManifestId" AND
                        (p."Id" IS NULL OR r."Id" IS NULL OR p."ReceiptId" <> r."Id"
                        OR p."DeletedAt" IS NOT NULL OR r."DeletedAt" IS NOT NULL
                        OR p."NormalizationRevision" <> i."ItemRevision" OR r."NormalizationRevision" <> i."ReceiptRevision"
                        OR (r."Location",p."Description",p."ReceiptItemCode") IS DISTINCT FROM (i."Store",i."RawDescription",i."ItemCode")
                        OR c."AssignmentId" IS DISTINCT FROM i."ExpectedAssignmentId"
                        OR (c."AssignmentId" IS NULL AND (taxonomy.effect_v1_metadata_text(p."Category")
                            OR taxonomy.effect_v1_metadata_text(coalesce(p."Subcategory",''))))
                        OR (a."Id" IS NOT NULL AND (a."CategoryId" IS NOT NULL OR NOT
                            (a."Source" = 'legacy_import' OR (a."Source" = 'legacy_write' AND a."LegacyResolution" = 'unresolved_path'))))))
                THEN RAISE EXCEPTION 'Capture source or prior assignment is no longer eligible' USING ERRCODE = '23514'; END IF;
                -- Historical operation/command IDs cannot be recycled as this capture's future phases.
                -- This checks visible history only. Uncoordinated ordinary writes may race or
                -- reuse IDs later: every writer must join a durable global namespace protocol
                -- before activation. This check does not provide cross-table exclusion.
                IF EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectOperations" e
                    JOIN (
                        SELECT "OperationId" FROM taxonomy."Revisions"
                        UNION ALL SELECT "OperationId" FROM taxonomy."Operations"
                        UNION ALL SELECT "OperationId" FROM taxonomy."CategoryMappingRevisions"
                        UNION ALL SELECT "OperationId" FROM taxonomy."Assignments"
                        UNION ALL SELECT "OperationId" FROM taxonomy."AssignmentOperations"
                        UNION ALL SELECT "Id" FROM taxonomy."HistoricalOperations"
                        UNION ALL SELECT "ApplyOperationId" FROM taxonomy."HistoricalOperationItems"
                        UNION ALL SELECT "UndoOperationId" FROM taxonomy."HistoricalOperationItems"
                        UNION ALL SELECT "OperationId" FROM taxonomy."HistoricalDispositionOperations"
                        UNION ALL SELECT "OperationId" FROM taxonomy."LegacyMigrations"
                        UNION ALL SELECT "OperationId" FROM taxonomy."LegacyCrosswalks"
                        UNION ALL SELECT "OperationId" FROM taxonomy."LegacyItemOutcomes"
                        UNION ALL SELECT "OperationId" FROM category_assistance."HistoricalRequests"
                        UNION ALL SELECT "OperationId" FROM category_assistance."ReviewOperations"
                        UNION ALL SELECT "DecisionOperationId" FROM category_assistance."Attempts"
                        UNION ALL SELECT "Id" FROM matching."NormalizationCatalogOperations"
                        UNION ALL SELECT "ClientRequestId" FROM matching."NormalizationCatalogOperations"
                        UNION ALL SELECT "RequestId" FROM receipts."ReceiptWriteRequests"
                    ) ordinary ON ordinary."OperationId" = e."OperationId"
                    WHERE e."ManifestId" = NEW."ManifestId")
                THEN RAISE EXCEPTION 'Effect operation identity is already used by ordinary history' USING ERRCODE = '23514'; END IF;
                -- Only database-owned frozen rows enter the canonical projection.
                NEW."CanonicalRequest" := taxonomy.effect_v1_relational(NEW."ManifestId",NEW."OrderedItemIds");
                NEW."RequestHash" := upper(encode(sha256(convert_to('receipts.organization-effect.manifest-request.v1','UTF8')
                    || decode('00','hex') || NEW."CanonicalRequest"),'hex'));
                IF manifest."RequestHash" IS DISTINCT FROM NEW."RequestHash" THEN
                    RAISE EXCEPTION 'Typed manifest hash differs from frozen rows' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER seal_effect_capture_v1 BEFORE INSERT ON taxonomy."OrganizationEffectCaptureWitnessesV1"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.seal_effect_capture_v1();
            CREATE FUNCTION taxonomy.guard_sealed_effect_member_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectCaptureWitnessesV1" WHERE "ManifestId" = NEW."ManifestId") THEN
                    RAISE EXCEPTION 'Typed capture membership is already sealed' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER guard_sealed_effect_member_v1 BEFORE INSERT ON taxonomy."OrganizationEffectItems"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_sealed_effect_member_v1();
            CREATE FUNCTION taxonomy.require_effect_capture_witness_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."CaptureWitnessVersion" = 1 THEN
                    IF TG_TABLE_NAME = 'OrganizationEffectManifests' THEN
                        IF NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectCaptureWitnessesV1" WHERE "ManifestId" = NEW."Id") THEN
                            RAISE EXCEPTION 'Typed manifest requires capture witness' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectCaptureWitnessesV1" WHERE "EventId" = NEW."Id") THEN
                            RAISE EXCEPTION 'Typed journal event requires capture witness' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END IF;
                RETURN NULL;
            END $$;
            -- Attached to the declarations, so a failed/caught seal cannot leave an unsealed typed capture.
            CREATE CONSTRAINT TRIGGER require_manifest_capture_witness_v1 AFTER INSERT ON taxonomy."OrganizationEffectManifests"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION taxonomy.require_effect_capture_witness_v1();
            CREATE CONSTRAINT TRIGGER require_event_capture_witness_v1 AFTER INSERT ON taxonomy."MappingAuthorityEvents"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION taxonomy.require_effect_capture_witness_v1();
            CREATE TRIGGER immutable_effect_capture_witness_v1 BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectCaptureWitnessesV1"
                FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
            CREATE TRIGGER retain_effect_capture_witness_v1 BEFORE TRUNCATE ON taxonomy."OrganizationEffectCaptureWitnessesV1"
                FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
        """);
	}

	protected override void Down(MigrationBuilder migrationBuilder) =>
		throw new NotSupportedException("Capture evidence is retained; roll forward instead.");
}
