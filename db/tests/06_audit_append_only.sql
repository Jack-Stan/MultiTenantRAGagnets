-- 06_audit_append_only.sql : RUN AS app_user, after 00_fixture.sql.
-- INSERT works for own tenant; UPDATE / DELETE / TRUNCATE are denied by GRANTs;
-- forged-tenant inserts and cross-tenant reads are blocked by RLS. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');

    INSERT INTO audit_log (tenant_id, user_id, role, query_hash,
                           retrieved_chunk_ids, sent_chunk_ids, model, latency_ms)
    VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'a0000000-0000-0000-0000-000000000001', 'employee',
            'sha256:test',
            ARRAY['c0000000-0000-0000-0000-0000000000a1', 'c0000000-0000-0000-0000-0000000000a2']::uuid[],
            ARRAY['c0000000-0000-0000-0000-0000000000a1']::uuid[],
            'fake', 5);
    PERFORM pg_temp.expect((SELECT count(*) FROM audit_log) >= 1,
                           'own-tenant audit row is insertable and readable');

    PERFORM pg_temp.expect(NOT has_table_privilege('audit_log', 'UPDATE'),   'no UPDATE grant on audit_log');
    PERFORM pg_temp.expect(NOT has_table_privilege('audit_log', 'DELETE'),   'no DELETE grant on audit_log');
    PERFORM pg_temp.expect(NOT has_table_privilege('audit_log', 'TRUNCATE'), 'no TRUNCATE grant on audit_log');

    PERFORM pg_temp.expect(pg_temp.denied('UPDATE audit_log SET model = ''tampered'''), 'UPDATE audit_log denied');
    PERFORM pg_temp.expect(pg_temp.denied('DELETE FROM audit_log'),                      'DELETE audit_log denied');
    PERFORM pg_temp.expect(pg_temp.denied('TRUNCATE audit_log'),                         'TRUNCATE audit_log denied');

    -- Forged tenant on insert: RLS WITH CHECK (42501).
    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO audit_log (tenant_id, user_id, role, query_hash, model, latency_ms)
        VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'b0000000-0000-0000-0000-000000000001',
                'employee', 'sha256:forged', 'fake', 1) $q$),
        'cannot write an audit row for another tenant');

    -- sent must be a subset of retrieved (CHECK constraint, 23514).
    BEGIN
        INSERT INTO audit_log (tenant_id, user_id, role, query_hash,
                               retrieved_chunk_ids, sent_chunk_ids, model, latency_ms)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'a0000000-0000-0000-0000-000000000001', 'employee',
                'sha256:bad', '{}', ARRAY['c0000000-0000-0000-0000-0000000000a1']::uuid[], 'fake', 1);
        RAISE EXCEPTION 'SMOKE TEST FAILED: sent not a subset of retrieved was accepted';
    EXCEPTION WHEN check_violation THEN
        NULL;  -- expected
    END;

    -- Tenant B cannot read A's audit rows.
    PERFORM pg_temp.as_caller(pg_temp.tenant_b(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM audit_log WHERE tenant_id = pg_temp.tenant_a()) = 0,
                           'tenant B sees 0 of tenant A audit rows');
END
$$;

ROLLBACK;
