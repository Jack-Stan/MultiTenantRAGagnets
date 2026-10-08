-- 02_tenant_isolation.sql : RUN AS app_user, after 00_fixture.sql.
-- Cross-tenant SELECT returns 0 rows, even for the top role. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    -- Tenant A hr-admin: sees A's 4 chunks / 3 docs, zero of B's.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks    WHERE tenant_id = pg_temp.tenant_b()) = 0,
                           'A hr-admin sees 0 of B''s chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents WHERE tenant_id = pg_temp.tenant_b()) = 0,
                           'A hr-admin sees 0 of B''s documents');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks)    = 4, 'A hr-admin sees exactly 4 chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = 3, 'A hr-admin sees exactly 3 documents');
    PERFORM pg_temp.expect((SELECT count(*) FROM tenants)   = 1, 'A sees only its own tenant row');
    PERFORM pg_temp.expect((SELECT count(*) FROM users)     = 3, 'A sees only its own users');

    -- Tenant B hr-admin: sees B's 2 chunks, zero of A's.
    PERFORM pg_temp.as_caller(pg_temp.tenant_b(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks WHERE tenant_id = pg_temp.tenant_a()) = 0,
                           'B hr-admin sees 0 of A''s chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 2, 'B hr-admin sees exactly 2 chunks');

    -- Nearest-neighbour bait: ask A for the 10 nearest to a query sitting on
    -- dimension 1, where B's rows live. A must get only its own 4.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect(
        (SELECT count(*) FROM (
            SELECT id FROM chunks
            ORDER BY embedding <=> (SELECT array_agg(CASE WHEN g = 1 THEN 1::real ELSE 0::real END ORDER BY g)::vector(768)
                                    FROM generate_series(1, 768) g)
            LIMIT 10) q) = 4,
        'A hr-admin ANN query returns only A''s 4 chunks');
END
$$;

ROLLBACK;
