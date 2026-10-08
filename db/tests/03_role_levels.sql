-- 03_role_levels.sql : RUN AS app_user, after 00_fixture.sql.
-- A too-low role gets 0 rows for higher-level docs; hr-admin inherits manager
-- and employee. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 2, 'A employee sees 2 chunks (L1 only)');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks WHERE text IN ('A manager memo', 'A salary bands')) = 0,
                           'A employee sees 0 manager/hr-admin chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents WHERE required_level > 1) = 0,
                           'A employee sees 0 documents above level 1');

    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 3, 'A manager sees 3 chunks (L1+L2)');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks WHERE text = 'A salary bands') = 0,
                           'A manager sees 0 hr-admin chunks');

    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 4, 'A hr-admin inherits employee+manager (4 chunks)');

    -- The app-layer filter (same shared function) must agree with RLS.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect(
        (SELECT count(*) FROM chunks c
           JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
          WHERE c.tenant_id = pg_temp.tenant_a()
            AND rag_level_allows(rag_role_level('manager'), d.required_level))
        = (SELECT count(*) FROM chunks),
        'app-layer filter and RLS agree for manager');

    -- Unknown role => NULL level => fail closed.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'intern');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 0, 'unknown role sees 0 chunks');
END
$$;

ROLLBACK;
