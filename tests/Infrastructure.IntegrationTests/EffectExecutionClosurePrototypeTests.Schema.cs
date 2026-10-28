namespace Infrastructure.IntegrationTests;

public sealed partial class EffectExecutionClosurePrototypeTests
{
	// A separate domain and JSONB encoding: intentionally NOT the production v1 canonical codec.
	private const string Schema = """
        CREATE TEMP TABLE witness (
            manifest uuid PRIMARY KEY, item uuid NOT NULL, actor text NOT NULL, source text NOT NULL,
            receipt_revision uuid NOT NULL, item_revision uuid NOT NULL, taxonomy uuid NOT NULL,
            mapping uuid NOT NULL, settings uuid NOT NULL, restore uuid NOT NULL,
            prior_id int NOT NULL, prior_category int, target int NOT NULL, capture_hash text NOT NULL);
        CREATE TEMP TABLE reservations (
            op uuid PRIMARY KEY, manifest uuid NOT NULL, item uuid NOT NULL,
            phase text NOT NULL, class text NOT NULL);
        CREATE TEMP TABLE live (
            current_id int NOT NULL, source text NOT NULL, receipt_revision uuid NOT NULL,
            item_revision uuid NOT NULL, taxonomy uuid NOT NULL, mapping uuid NOT NULL,
            settings uuid NOT NULL, restore uuid NOT NULL, enabled bool NOT NULL,
            provider text, available bool NOT NULL, suppressed bool NOT NULL,
            leave_as_is bool NOT NULL, selectable bool NOT NULL);
        CREATE TEMP TABLE assignments (
            id int PRIMARY KEY, previous_id int, category int, source text NOT NULL, op uuid);
        CREATE TEMP TABLE claims (
            op uuid PRIMARY KEY, manifest uuid NOT NULL, item uuid NOT NULL, actor text NOT NULL,
            phase text NOT NULL CHECK (phase IN ('apply','undo')), previous_id int NOT NULL,
            assignment_id int NOT NULL UNIQUE, category int, capture_hash text NOT NULL,
            phase_hash text NOT NULL, claim_hash text NOT NULL, created_tx bigint NOT NULL);
        CREATE TEMP TABLE operations (
            op uuid PRIMARY KEY, assignment_id int NOT NULL UNIQUE, actor text NOT NULL, claim_hash text NOT NULL);
        CREATE TEMP TABLE outcomes (
            op uuid PRIMARY KEY, assignment_id int NOT NULL UNIQUE, phase text NOT NULL,
            terminal text NOT NULL CHECK (terminal='applied'), claim_hash text NOT NULL);
        INSERT INTO witness VALUES (gen_random_uuid(),gen_random_uuid(),'owner','raw',gen_random_uuid(),
            gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),1,10,20,'synthetic-capture');
        INSERT INTO reservations SELECT gen_random_uuid(),manifest,item,'apply','effect' FROM witness;
        INSERT INTO reservations SELECT gen_random_uuid(),manifest,item,'undo','effect' FROM witness;
        INSERT INTO live SELECT 1,source,receipt_revision,item_revision,taxonomy,mapping,settings,restore,
            true,'synthetic-provider',true,false,false,true FROM witness;
        INSERT INTO assignments VALUES (1,NULL,10,'imported',NULL);

        CREATE FUNCTION pg_temp.make_claim(p_phase text,p_actor text,p_source text,p_hash text) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE w pg_temp.witness%ROWTYPE; s pg_temp.live%ROWTYPE;
            r pg_temp.reservations%ROWTYPE; a pg_temp.assignments%ROWTYPE;
            previous_claim pg_temp.claims%ROWTYPE; new_id int; category_id int; digest text;
        BEGIN
            -- Synthetic single-item serialization only; not the production lock protocol.
            SELECT * INTO STRICT w FROM pg_temp.witness;
            SELECT * INTO STRICT s FROM pg_temp.live FOR UPDATE;
            SELECT * INTO STRICT r FROM pg_temp.reservations WHERE phase=p_phase;
            SELECT * INTO STRICT a FROM pg_temp.assignments WHERE id=s.current_id;
            IF p_phase NOT IN ('apply','undo') OR p_actor IS DISTINCT FROM w.actor
                OR p_source IS DISTINCT FROM w.source OR p_hash IS DISTINCT FROM 'phase-'||p_phase
                OR (r.manifest,r.item,r.class) IS DISTINCT FROM (w.manifest,w.item,'effect'::text)
                OR (s.source,s.receipt_revision,s.item_revision,s.taxonomy,s.restore)
                    IS DISTINCT FROM (w.source,w.receipt_revision,w.item_revision,w.taxonomy,w.restore)
                OR NOT s.available OR NOT s.selectable
            THEN RAISE EXCEPTION 'synthetic binding/fence mismatch' USING ERRCODE='23514'; END IF;
            IF p_phase='apply' THEN
                IF NOT s.enabled OR s.suppressed OR s.leave_as_is
                    OR s.settings<>w.settings OR s.mapping<>w.mapping OR a.id<>w.prior_id
                    OR a.source='manual' OR a.category IS DISTINCT FROM w.prior_category
                THEN RAISE EXCEPTION 'apply protected/stale' USING ERRCODE='23514'; END IF;
                new_id:=2; category_id:=w.target;
            ELSE
                SELECT c.* INTO STRICT previous_claim FROM pg_temp.claims c
                    JOIN pg_temp.outcomes o USING(op)
                    WHERE c.phase='apply' AND c.manifest=w.manifest AND c.item=w.item
                        AND o.claim_hash=c.claim_hash AND o.assignment_id=c.assignment_id;
                IF a.id<>previous_claim.assignment_id OR a.op<>previous_claim.op
                    OR a.source<>'automatic' OR a.previous_id IS DISTINCT FROM w.prior_id
                    OR a.category IS DISTINCT FROM w.target
                    OR NOT EXISTS (SELECT 1 FROM pg_temp.assignments
                        WHERE id=w.prior_id AND category IS NOT DISTINCT FROM w.prior_category)
                THEN RAISE EXCEPTION 'undo owner/lineage changed' USING ERRCODE='23514'; END IF;
                new_id:=3; category_id:=w.prior_category;
            END IF;
            -- Typed relational claim, derived here; no session flag or caller-supplied assignment authority.
            digest:=upper(encode(sha256(convert_to('test-only.execution-closure'||chr(1)||
                jsonb_build_array(r.op,w.manifest,w.item,w.actor,p_phase,a.id,new_id,category_id,
                    w.capture_hash,p_hash)::text,'UTF8')),'hex'));
            INSERT INTO pg_temp.claims VALUES (r.op,w.manifest,w.item,w.actor,p_phase,a.id,new_id,
                category_id,w.capture_hash,p_hash,digest,txid_current());
        END $$;

        CREATE FUNCTION pg_temp.run_effect(p_phase text,p_actor text,p_source text,p_hash text) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE c pg_temp.claims%ROWTYPE;
        BEGIN
            PERFORM pg_temp.make_claim(p_phase,p_actor,p_source,p_hash);
            SELECT * INTO STRICT c FROM pg_temp.claims WHERE phase=p_phase;
            INSERT INTO pg_temp.assignments VALUES (c.assignment_id,c.previous_id,c.category,'automatic',c.op);
            INSERT INTO pg_temp.operations VALUES(c.op,c.assignment_id,c.actor,c.claim_hash);
            UPDATE pg_temp.live SET current_id=c.assignment_id;
            INSERT INTO pg_temp.outcomes VALUES(c.op,c.assignment_id,c.phase,'applied',c.claim_hash);
        END $$;

        CREATE FUNCTION pg_temp.check_closure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- Full symmetric closure: claim alone AND orphan assignment/operation/outcome reject.
            IF EXISTS (SELECT 1 FROM pg_temp.claims c
                LEFT JOIN pg_temp.witness w ON w.manifest=c.manifest AND w.item=c.item
                LEFT JOIN pg_temp.reservations r ON r.op=c.op
                LEFT JOIN pg_temp.assignments a ON a.id=c.assignment_id
                LEFT JOIN pg_temp.operations o ON o.op=c.op
                LEFT JOIN pg_temp.outcomes t ON t.op=c.op
                WHERE w.manifest IS NULL OR r.op IS NULL OR a.id IS NULL OR o.op IS NULL OR t.op IS NULL
                    OR (r.manifest,r.item,r.phase,r.class) IS DISTINCT FROM (c.manifest,c.item,c.phase,'effect'::text)
                    OR c.actor IS DISTINCT FROM w.actor OR c.capture_hash IS DISTINCT FROM w.capture_hash
                    OR c.phase_hash IS DISTINCT FROM 'phase-'||c.phase
                    OR (c.phase='apply' AND (c.previous_id,c.category) IS DISTINCT FROM (w.prior_id,w.target))
                    OR (c.phase='undo' AND c.category IS DISTINCT FROM w.prior_category)
                    OR (a.previous_id,a.category,a.source,a.op) IS DISTINCT FROM
                        (c.previous_id,c.category,'automatic'::text,c.op)
                    OR (o.assignment_id,o.actor,o.claim_hash) IS DISTINCT FROM (c.assignment_id,c.actor,c.claim_hash)
                    OR (t.assignment_id,t.phase,t.claim_hash) IS DISTINCT FROM (c.assignment_id,c.phase,c.claim_hash)
                    OR c.claim_hash IS DISTINCT FROM upper(encode(sha256(convert_to('test-only.execution-closure'||chr(1)||
                        jsonb_build_array(c.op,c.manifest,c.item,c.actor,c.phase,c.previous_id,c.assignment_id,
                            c.category,c.capture_hash,c.phase_hash)::text,'UTF8')),'hex')))
                OR EXISTS (SELECT 1 FROM pg_temp.assignments a WHERE a.source='automatic'
                    AND NOT EXISTS (SELECT 1 FROM pg_temp.claims c WHERE c.assignment_id=a.id))
                OR EXISTS (SELECT 1 FROM pg_temp.operations o WHERE NOT EXISTS
                    (SELECT 1 FROM pg_temp.claims c WHERE c.op=o.op))
                OR EXISTS (SELECT 1 FROM pg_temp.outcomes o WHERE NOT EXISTS
                    (SELECT 1 FROM pg_temp.claims c WHERE c.op=o.op))
                OR (SELECT count(*) FROM pg_temp.live) <> 1
                OR EXISTS (SELECT 1 FROM pg_temp.live s WHERE NOT EXISTS
                    (SELECT 1 FROM pg_temp.assignments a WHERE a.id=s.current_id))
                OR EXISTS (SELECT 1 FROM pg_temp.claims c WHERE c.created_tx=txid_current() AND c.assignment_id=
                    (SELECT max(assignment_id) FROM pg_temp.claims) AND NOT EXISTS
                    (SELECT 1 FROM pg_temp.live s WHERE s.current_id=c.assignment_id))
            THEN RAISE EXCEPTION 'incomplete synthetic effect closure' USING ERRCODE='23514'; END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER closure_claim AFTER INSERT OR UPDATE OR DELETE ON pg_temp.claims
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pg_temp.check_closure();
        CREATE CONSTRAINT TRIGGER closure_assignment AFTER INSERT OR UPDATE OR DELETE ON pg_temp.assignments
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pg_temp.check_closure();
        CREATE CONSTRAINT TRIGGER closure_operation AFTER INSERT OR UPDATE OR DELETE ON pg_temp.operations
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pg_temp.check_closure();
        CREATE CONSTRAINT TRIGGER closure_outcome AFTER INSERT OR UPDATE OR DELETE ON pg_temp.outcomes
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pg_temp.check_closure();
        """;
}
