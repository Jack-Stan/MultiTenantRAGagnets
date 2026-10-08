-- 09_write_roles.sql : RUN AS app_user, after 00_fixture.sql.
-- 005_write_policies.sql: READ is level-based for everyone, WRITE (INSERT / UPDATE /
-- DELETE) on documents AND chunks needs hr-admin level. Mirrors EOGHAN's Findings
-- 12-A / 12-B at the database layer.
--   * INSERT denial      => ERROR 42501 (RLS WITH CHECK)
--   * UPDATE/DELETE denial => the row fails USING, so 0 rows affected, no error.
--     Each such denial is ALSO followed by a read proving the row is unchanged.
-- The whole file rolls back. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

-- ---------------------------------------------------------------------------
-- (a) employee and manager: every write denied, reads unchanged.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    r        text;
    v_doc    uuid;
    v_chunk  uuid;
    v_level  integer;
    v_docs   bigint;
    v_chunks bigint;
BEGIN
    FOREACH r IN ARRAY ARRAY['employee', 'manager'] LOOP
        PERFORM pg_temp.as_caller(pg_temp.tenant_a(), r);
        -- Each role's own-level document and one of its chunks (both fixture rows).
        IF r = 'employee' THEN
            v_doc := 'd0c00000-0000-0000-0000-0000000000a1'; v_chunk := 'c0000000-0000-0000-0000-0000000000a1'; v_level := 1;
        ELSE
            v_doc := 'd0c00000-0000-0000-0000-0000000000a2'; v_chunk := 'c0000000-0000-0000-0000-0000000000a3'; v_level := 2;
        END IF;
        v_docs   := (SELECT count(*) FROM documents);
        v_chunks := (SELECT count(*) FROM chunks);
        PERFORM pg_temp.expect(v_docs = v_level AND v_chunks = v_level + 1,
                               r || ': read counts are the level-based ones (' || v_docs || ' docs, ' || v_chunks || ' chunks)');

        -- INSERT documents (own level, lower level, own tenant): 42501.
        PERFORM pg_temp.expect(pg_temp.denied_42501(format($q$
            INSERT INTO documents (tenant_id, title, required_level)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'planted by %s', %s) $q$, r, v_level)),
            r || ' cannot INSERT a document at own level');
        PERFORM pg_temp.expect(pg_temp.denied_42501($q$
            INSERT INTO documents (tenant_id, title, required_level)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'planted low', 1) $q$),
            r || ' cannot INSERT a level-1 document');

        -- Ingest upsert over an existing document: the INSERT half is refused first.
        PERFORM pg_temp.expect(pg_temp.denied_42501(format($q$
            INSERT INTO documents (id, tenant_id, title, required_level)
            VALUES (%L, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'upserted by lower role', %s)
            ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title $q$, v_doc, v_level)),
            r || ' cannot upsert over an existing document');

        -- INSERT chunk under a document the role can read: 42501.
        PERFORM pg_temp.expect(pg_temp.denied_42501(format($q$
            INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding)
            VALUES (%L, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 9, 'planted chunk', pg_temp.anyvec()) $q$, v_doc)),
            r || ' cannot INSERT a chunk under a readable document');

        -- UPDATE documents: rename, and change the ACL both ways (12-A / 12-B).
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'UPDATE documents SET title = ''tampered'' WHERE id = %L', v_doc)) = 0,
            r || ' cannot rename a document (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'UPDATE documents SET required_level = 1 WHERE id = %L', v_doc)) = 0,
            r || ' cannot set a document to level 1 (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'UPDATE documents SET required_level = 2 WHERE id = %L', v_doc)) = 0,
            r || ' cannot set a document to level 2 (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'UPDATE documents SET required_level = 3 WHERE id = %L', v_doc)) = 0,
            r || ' cannot set a document to level 3 (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected('UPDATE documents SET title = ''tampered''') = 0,
            r || ' cannot bulk-update documents (0 rows)');

        -- UPDATE / DELETE chunks.
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'UPDATE chunks SET text = ''tampered'' WHERE id = %L', v_chunk)) = 0,
            r || ' cannot rewrite a chunk (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'DELETE FROM chunks WHERE id = %L', v_chunk)) = 0,
            r || ' cannot delete a chunk (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected('DELETE FROM chunks') = 0,
            r || ' cannot bulk-delete chunks (0 rows)');

        -- DELETE document (would cascade its chunks).
        PERFORM pg_temp.expect(pg_temp.rows_affected(format(
            'DELETE FROM documents WHERE id = %L', v_doc)) = 0,
            r || ' cannot delete a document (0 rows)');
        PERFORM pg_temp.expect(pg_temp.rows_affected('DELETE FROM documents') = 0,
            r || ' cannot bulk-delete documents (0 rows)');

        -- Nothing changed: same counts, same title and level, same chunk text.
        PERFORM pg_temp.expect((SELECT count(*) FROM documents) = v_docs, r || ': document count unchanged');
        PERFORM pg_temp.expect((SELECT count(*) FROM chunks)    = v_chunks, r || ': chunk count unchanged');
        PERFORM pg_temp.expect(
            (SELECT required_level FROM documents WHERE id = v_doc) = v_level
            AND (SELECT title FROM documents WHERE id = v_doc) NOT IN ('tampered', 'upserted by lower role'),
            r || ': document title and level unchanged');
        PERFORM pg_temp.expect((SELECT text FROM chunks WHERE id = v_chunk) <> 'tampered', r || ': chunk text unchanged');
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------
-- (b) hr-admin A: the full ingest / access-change path works inside tenant A.
-- ---------------------------------------------------------------------------
DO $$
DECLARE n bigint;
BEGIN
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');

    -- INSERT documents at every level.
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        INSERT INTO documents (id, tenant_id, title, required_level) VALUES
          ('d0c00000-0000-0000-0000-0000000000f1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'new L1', 1),
          ('d0c00000-0000-0000-0000-0000000000f2', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'new L2', 2),
          ('d0c00000-0000-0000-0000-0000000000f3', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'new L3', 3) $q$) = 3,
        'hr-admin A can insert documents at levels 1, 2 and 3');

    -- INSERT chunks, UPDATE a chunk, DELETE a chunk.
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding)
        VALUES ('c0000000-0000-0000-0000-0000000000f1', 'd0c00000-0000-0000-0000-0000000000f1',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0, 'new chunk', pg_temp.anyvec()) $q$) = 1,
        'hr-admin A can insert a chunk');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'UPDATE chunks SET text = ''edited'' WHERE id = ''c0000000-0000-0000-0000-0000000000f1''') = 1,
        'hr-admin A can update a chunk');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'DELETE FROM chunks WHERE id = ''c0000000-0000-0000-0000-0000000000f1''') = 1,
        'hr-admin A can delete a chunk');

    -- The API ingest path: ON CONFLICT upsert of the document, DELETE its old chunks,
    -- INSERT the new ones (StoreDocumentAsync).
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        INSERT INTO documents (id, tenant_id, title, required_level, version)
        VALUES ('d0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'A handbook v2', 1, 2)
        ON CONFLICT (id) DO UPDATE
           SET title = EXCLUDED.title, required_level = EXCLUDED.required_level,
               version = EXCLUDED.version, updated_at = now() $q$) = 1,
        'hr-admin A: ingest upsert (ON CONFLICT DO UPDATE) works');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'DELETE FROM chunks WHERE doc_id = ''d0c00000-0000-0000-0000-0000000000a1''') = 2,
        'hr-admin A: ingest replaces the old chunks (2 deleted)');
    PERFORM pg_temp.expect(pg_temp.rows_affected($q$
        INSERT INTO chunks (doc_id, tenant_id, chunk_index, text, embedding, version) VALUES
          ('d0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0, 'v2 part 1', pg_temp.anyvec(), 2),
          ('d0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 1, 'v2 part 2', pg_temp.anyvec(), 2),
          ('d0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 2, 'v2 part 3', pg_temp.anyvec(), 2) $q$) = 3,
        'hr-admin A: ingest inserts the new chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks WHERE doc_id = 'd0c00000-0000-0000-0000-0000000000a1') = 3,
                           'hr-admin A reads back the re-ingested chunks');

    -- Access change (UPDATE required_level) both ways.
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'UPDATE documents SET required_level = 2 WHERE id = ''d0c00000-0000-0000-0000-0000000000a1''') = 1,
        'hr-admin A can raise a document to level 2');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'UPDATE documents SET required_level = 1 WHERE id = ''d0c00000-0000-0000-0000-0000000000a1''') = 1,
        'hr-admin A can lower a document to level 1');

    -- DELETE a document: its chunks go with it (FK cascade).
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        'DELETE FROM documents WHERE id = ''d0c00000-0000-0000-0000-0000000000a1''') = 1,
        'hr-admin A can delete a document');
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks WHERE doc_id = 'd0c00000-0000-0000-0000-0000000000a1') = 0,
                           'deleting the document cascaded its chunks');

    -- Reads for hr-admin are still the level-based ones (a2 + a3 + new L1/L2/L3 docs).
    SELECT count(*) INTO n FROM documents;
    PERFORM pg_temp.expect(n = 5, 'hr-admin A sees 5 documents after the writes above, got ' || n);

    -- Tenant B was not touched by any of it.
    PERFORM pg_temp.as_caller(pg_temp.tenant_b(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = 2 AND (SELECT count(*) FROM chunks) = 2,
                           'tenant B untouched by tenant A hr-admin writes');
END
$$;

-- ---------------------------------------------------------------------------
-- (c) No context / unknown role: writes fail closed too.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    -- Unknown role => NULL level => rag_can_write() false.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'intern');
    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'intern doc', 1) $q$),
        'unknown role cannot INSERT');
    PERFORM pg_temp.expect(pg_temp.rows_affected('DELETE FROM documents') = 0, 'unknown role deletes 0 documents');

    -- Role level set but no tenant, and tenant set but no role level.
    PERFORM set_config('app.tenant_id', '', true);
    PERFORM set_config('app.role_level', '3', true);
    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'no tenant', 1) $q$),
        'hr-admin level without a tenant cannot INSERT');
    PERFORM pg_temp.expect(pg_temp.rows_affected('UPDATE documents SET title = ''x''') = 0, 'no tenant: UPDATE touches 0 rows');
    PERFORM set_config('app.tenant_id', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', true);
    PERFORM set_config('app.role_level', '', true);
    PERFORM pg_temp.expect(pg_temp.denied_42501($q$
        INSERT INTO documents (tenant_id, title, required_level)
        VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'no role', 1) $q$),
        'tenant without a role level cannot INSERT');
    PERFORM pg_temp.expect(pg_temp.rows_affected('DELETE FROM chunks') = 0, 'no role level: DELETE touches 0 rows');
END
$$;

-- ---------------------------------------------------------------------------
-- (d) Structure: rag_can_write exists, is the policies' gate, and FORCE RLS holds.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect(rag_can_write(), 'rag_can_write() is true for hr-admin');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect(NOT rag_can_write(), 'rag_can_write() is false for manager');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'intern');
    PERFORM pg_temp.expect(NOT rag_can_write(), 'rag_can_write() is false (not NULL) for an unknown role');

    PERFORM pg_temp.expect(
        (SELECT count(*) FROM pg_policies
          WHERE schemaname = 'public' AND tablename IN ('documents', 'chunks')
            AND (qual LIKE '%rag_can_write%' OR with_check LIKE '%rag_can_write%')
            AND cmd IN ('INSERT', 'UPDATE', 'DELETE')) = 6,
        'all 6 write policies on documents/chunks are gated by rag_can_write()');
    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM pg_policies
                     WHERE schemaname = 'public' AND tablename IN ('documents', 'chunks')
                       AND cmd = 'SELECT'
                       AND (qual LIKE '%rag_can_write%' OR with_check LIKE '%rag_can_write%')),
        'READ policies are not gated by rag_can_write()');
END
$$;

ROLLBACK;
