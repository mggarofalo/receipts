using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

// INACTIVE: no producer opts into this additional capture attestation.
public sealed partial class AddOrganizationEffectCaptureFencesV1 : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE taxonomy."OrganizationEffectCaptureWitnessesV1"
            ADD COLUMN "CaptureFenceVersion" integer NULL CHECK ("CaptureFenceVersion" = 1);
        CREATE TABLE taxonomy."OrganizationEffectCaptureFencesV1" (
            "ManifestId" uuid PRIMARY KEY REFERENCES taxonomy."OrganizationEffectCaptureWitnessesV1" ("ManifestId"),
            "Version" integer NOT NULL CHECK ("Version" = 1),
            "SettingsRevision" uuid NOT NULL,
            "RestoreGeneration" uuid NOT NULL,
            "CanonicalEvidence" bytea NOT NULL,
            "EvidenceHash" text NOT NULL COLLATE "C"
        );
        CREATE FUNCTION taxonomy.lock_effect_capture_policy_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW."CaptureFenceVersion" IS NULL THEN RETURN NEW; END IF;
            IF current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'Capture fence requires READ COMMITTED' USING ERRCODE = '25000';
            END IF;
            -- Transaction entry must acquire RCPT/NORM before baseline/mapping/Head work.
            -- Typed declarations already do so. Do not move Settings before Head:
            -- restore, dispatch, enrollment and discovery take Head -> Settings.
            PERFORM pg_advisory_xact_lock(1380143188,1313821261);
            PERFORM 1 FROM taxonomy."Head" WHERE "Id" = 1 FOR UPDATE;
            PERFORM 1 FROM category_assistance."Settings" WHERE "Id" = 1 FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Capture policy missing' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $$;
        -- Runs before seal_effect_capture_v1 takes receipt/item row locks.
        CREATE TRIGGER capture_policy_lock_v1 BEFORE INSERT ON taxonomy."OrganizationEffectCaptureWitnessesV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.lock_effect_capture_policy_v1();
        CREATE FUNCTION taxonomy.seal_effect_capture_policy_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE settings category_assistance."Settings"%ROWTYPE; canonical bytea;
        BEGIN
            IF NEW."CaptureFenceVersion" IS NULL THEN RETURN NULL; END IF;
            SELECT * INTO STRICT settings FROM category_assistance."Settings" WHERE "Id" = 1;
            IF NOT settings."Enabled" OR settings."Revision" = '00000000-0000-0000-0000-000000000000'
                OR settings."RestoreGeneration" = '00000000-0000-0000-0000-000000000000'
                OR settings."DailyLimit" NOT BETWEEN 1 AND 5000 OR settings."PendingLimit" NOT BETWEEN 1 AND 20
                OR NOT taxonomy.effect_v1_metadata_text(coalesce(settings."Provider",''))
                OR NOT taxonomy.effect_v1_metadata_text(coalesce(settings."Model",''))
            THEN RAISE EXCEPTION 'Capture policy disabled or invalid' USING ERRCODE = '23514'; END IF;
            canonical := convert_to(taxonomy.effect_v1_obj('Version','1',
                'ManifestId',taxonomy.effect_v1_q(NEW."ManifestId"::text),
                'ManifestRequestHash',taxonomy.effect_v1_q(NEW."RequestHash"),
                'SettingsRevision',taxonomy.effect_v1_q(settings."Revision"::text),
                'RestoreGeneration',taxonomy.effect_v1_q(settings."RestoreGeneration"::text)), 'UTF8');
            INSERT INTO taxonomy."OrganizationEffectCaptureFencesV1"
                ("ManifestId","Version","SettingsRevision","RestoreGeneration","CanonicalEvidence","EvidenceHash")
            VALUES (NEW."ManifestId",1,settings."Revision",settings."RestoreGeneration",canonical,
                upper(encode(sha256(convert_to('receipts.organization-effect.capture-fences.v1','UTF8')
                    || decode('00','hex') || canonical),'hex')));
            RETURN NULL;
        END $$;
        CREATE TRIGGER seal_effect_capture_policy_v1 AFTER INSERT ON taxonomy."OrganizationEffectCaptureWitnessesV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.seal_effect_capture_policy_v1();
        -- Only the witness trigger can supply facts. Top-level INSERT cannot fabricate a seal.
        CREATE FUNCTION taxonomy.guard_effect_capture_policy_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF pg_trigger_depth() <> 2 THEN
                RAISE EXCEPTION 'Capture fences are generated only by witness sealing' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER guard_effect_capture_policy_v1 BEFORE INSERT ON taxonomy."OrganizationEffectCaptureFencesV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.guard_effect_capture_policy_v1();
        CREATE TRIGGER immutable_effect_capture_policy_v1 BEFORE UPDATE OR DELETE ON taxonomy."OrganizationEffectCaptureFencesV1"
            FOR EACH ROW EXECUTE FUNCTION taxonomy.reject_history_mutation();
        CREATE TRIGGER retain_effect_capture_policy_v1 BEFORE TRUNCATE ON taxonomy."OrganizationEffectCaptureFencesV1"
            FOR EACH STATEMENT EXECUTE FUNCTION taxonomy.reject_history_mutation();
        """);

	protected override void Down(MigrationBuilder migrationBuilder) =>
		throw new NotSupportedException("Capture fences are retained; roll forward instead.");
}

