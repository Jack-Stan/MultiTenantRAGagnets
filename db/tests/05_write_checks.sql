-- 05_write_checks.sql : RUN AS app_user, after 00_fixture.sql.
-- WITH CHECK stops ingest from mislabelling tenant / level. A denial is caught
-- by pg_temp.denied() and asserted; the whole file rolls back.
-- Expected denial SQLSTATE: 42501 (RLS WITH CHECK or privilege) or 23503 (FK). UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    -- Caller: tenant A employee.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');

    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'planted in B', 1) $q$),
        'employee A cannot insert a document into tenant B');

    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'above my level', 3) $q$),
        'employee A cannot create a level-3 document');

    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('d0c00000-0000-0000-0000-0000000000b1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 9, 'x',
                (SELECT embedding FROM chunks LIMIT 1)) $q$),
        'employee A cannot insert a chunk under tenant B');

    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('d0c00000-0000-0000-0000-0000000000a1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 9, 'x',
                (SELECT embedding FROM chunks LIMIT 1)) $q$),
        'a chunk labelled with another tenant than its document is denied');

    -- UPDATE that would raise a visible document above the caller's level.
    PERFORM pg_temp.expect(pg_temp.denied($q$
        UPDATE documents SET required_level = 3
        WHERE id = 'd0c00000-0000-0000-0000-0000000000a1' $q$),
        'employee cannot raise a document above own level (WITH CHECK on the new row)');

    -- UPDATE/DELETE of rows the caller cannot see affects 0 rows (no error).
    UPDATE documents SET title = 'hacked' WHERE id = 'd0c00000-0000-0000-0000-0000000000b1';
    PERFORM pg_temp.expect(NOT FOUND, 'employee A updated 0 rows of a tenant B document');
    DELETE FROM chunks WHERE id = 'c0000000-0000-0000-0000-0000000000b1';
    PERFORM pg_temp.expect(NOT FOUND, 'employee A deleted 0 rows of a tenant B chunk');
    DELETE FROM chunks WHERE id = 'c0000000-0000-0000-0000-0000000000a4';  -- A's level-3 chunk
    PERFORM pg_temp.expect(NOT FOUND, 'employee A deleted 0 rows of a level-3 chunk');

    -- hr-admin A: access change is visible on the next query with no re-embed
    -- (the ACL lives on the document, not the chunk).
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    UPDATE documents SET required_level = 3
     WHERE id = 'd0c00000-0000-0000-0000-0000000000a2';
    PERFORM pg_temp.expect(FOUND, 'hr-admin A can change a document level');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 2,
                           'after raising a2 to L3, manager sees 2 chunks (revoke effective, no re-embed)');
END
$$;

ROLLBACK;
