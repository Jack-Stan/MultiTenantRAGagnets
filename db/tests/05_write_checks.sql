-- 05_write_checks.sql : RUN AS app_user, after 00_fixture.sql.
-- WITH CHECK / USING stop a WRITER from mislabelling or moving data across tenants,
-- and an ACL change takes effect with no re-embed. Writes now need hr-admin level
-- (005_write_policies.sql), so every "tenant wall" probe below runs as the ONLY role
-- allowed to write (hr-admin A): a denial is then proven to be the tenant wall, not
-- the role gate. The role gate itself (employee/manager denied everything) is 09_*.
-- Cross-tenant denials must be SQLSTATE 42501 (RLS), not a foreign key (23503):
-- pg_temp.denied_42501 accepts nothing else. The whole file rolls back. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    -- Caller: tenant A hr-admin (may write in A, nowhere else).
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');

    -- Control: a well-formed own-tenant write IS accepted, so the denials below are
    -- not just a broken statement.
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'control doc', 1) $q$) = 1,
        'control: hr-admin A can insert a document into tenant A');

    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'planted in B', 1) $q$),
        'hr-admin A cannot insert a document into tenant B (42501)');

    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('d0c00000-0000-0000-0000-0000000000b1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 9, 'x',
                pg_temp.anyvec()) $q$),
        'hr-admin A cannot insert a chunk under tenant B (42501)');

    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('d0c00000-0000-0000-0000-0000000000a1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 9, 'x',
                pg_temp.anyvec()) $q$),
        'a chunk labelled with another tenant than its document is denied (42501)');

    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('d0c00000-0000-0000-0000-0000000000b1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 9, 'x',
                pg_temp.anyvec()) $q$),
        'a tenant-A-labelled chunk pointing at tenant B''s document is denied (42501, document invisible)');

    -- Cross-tenant MOVE by hr-admin: USING passes on the old row, WITH CHECK fails on
    -- the new one. Must be RLS (42501), not the chunks->documents FK.
    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        UPDATE documents SET tenant_id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
        WHERE id = 'd0c00000-0000-0000-0000-0000000000a1' $q$),
        'hr-admin A cannot move a document to tenant B (42501, not an FK error)');
    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        UPDATE chunks SET tenant_id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
        WHERE id = 'c0000000-0000-0000-0000-0000000000a1' $q$),
        'hr-admin A cannot move a chunk to tenant B (42501)');

    -- Pulling or touching ANOTHER tenant's rows: invisible, so 0 rows, never an error
    -- and never a silent steal.
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        UPDATE documents SET tenant_id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
        WHERE id = 'd0c00000-0000-0000-0000-0000000000b1' $q$) = 0,
        'hr-admin A cannot pull a tenant B document into A (0 rows)');
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        UPDATE documents SET title = 'hacked'
        WHERE id = 'd0c00000-0000-0000-0000-0000000000b1' $q$) = 0,
        'hr-admin A updated 0 rows of a tenant B document');
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        DELETE FROM chunks WHERE id = 'c0000000-0000-0000-0000-0000000000b1' $q$) = 0,
        'hr-admin A deleted 0 rows of a tenant B chunk');
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        DELETE FROM documents WHERE tenant_id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' $q$) = 0,
        'hr-admin A deleted 0 rows of tenant B documents');

    -- Ingest upsert pointed at tenant B's document id: that row is invisible to A, the
    -- conflicting insert is refused rather than overwriting it.
    PERFORM pg_temp.expect(pg_temp.denied($q$
        INSERT INTO documents (id, tenant_id, title, required_level)
        VALUES ('d0c00000-0000-0000-0000-0000000000b1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'steal', 1)
        ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, tenant_id = EXCLUDED.tenant_id $q$),
        'upsert cannot take over a tenant B document id');

    -- hr-admin A: access change is visible on the next query with no re-embed
    -- (the ACL lives on the document, not the chunk).
    UPDATE documents SET required_level = 3
     WHERE id = 'd0c00000-0000-0000-0000-0000000000a2';
    PERFORM pg_temp.expect(FOUND, 'hr-admin A can change a document level');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 2,
                           'after raising a2 to L3, manager sees 2 chunks (revoke effective, no re-embed)');

    -- The other tenant is untouched by all of the above.
    PERFORM pg_temp.as_caller(pg_temp.tenant_b(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = 2 AND (SELECT count(*) FROM chunks) = 2,
                           'tenant B still has its 2 documents and 2 chunks');
    PERFORM pg_temp.expect((SELECT title FROM documents WHERE id = 'd0c00000-0000-0000-0000-0000000000b1') = 'B handbook',
                           'tenant B document title unchanged');
END
$$;

ROLLBACK;
