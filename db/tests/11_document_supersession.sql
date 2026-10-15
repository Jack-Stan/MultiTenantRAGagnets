-- 11_document_supersession.sql : RUN AS app_user, after 00_fixture.sql.
-- 006_document_supersession.sql adds documents.superseded_at (NULL = current), a RANKING signal.
-- This pins the three things that must stay true about it:
--   (a) it is only a column: every existing document is current and nothing about visibility moved;
--   (b) setting it needs the same write rights as any other document change (005): an employee or
--       manager, or an hr-admin of another tenant, touches 0 rows; an hr-admin of the tenant touches 1;
--   (c) setting it does NOT change what any role can read (it is never in a policy).
-- The whole file rolls back. UNVERIFIED until the first CI run that applies 006.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
DECLARE
    v_emp_docs_before   bigint;
    v_emp_chunks_before bigint;
    v_hr_docs_before    bigint;
BEGIN
    PERFORM pg_temp.expect(
        EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'documents'
                  AND column_name = 'superseded_at' AND data_type = 'timestamp with time zone'
                  AND is_nullable = 'YES' AND column_default IS NULL),
        'documents.superseded_at exists, timestamptz, nullable, no default');

    -- (a) every fixture document is current.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents WHERE superseded_at IS NOT NULL) = 0,
                           'no fixture document is superseded');
    v_hr_docs_before := (SELECT count(*) FROM documents);

    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');
    v_emp_docs_before   := (SELECT count(*) FROM documents);
    v_emp_chunks_before := (SELECT count(*) FROM chunks);

    -- (b) employee and manager: 0 rows, nothing changed.
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        $q$ UPDATE documents SET superseded_at = now() WHERE id = 'd0c00000-0000-0000-0000-0000000000a1' $q$) = 0,
        'employee cannot mark a document superseded');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'manager');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        $q$ UPDATE documents SET superseded_at = now() WHERE id = 'd0c00000-0000-0000-0000-0000000000a2' $q$) = 0,
        'manager cannot mark a document superseded');

    -- hr-admin of ANOTHER tenant: the tenant wall hides the row.
    PERFORM pg_temp.as_caller(pg_temp.tenant_b(), 'hr-admin');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        $q$ UPDATE documents SET superseded_at = now() WHERE id = 'd0c00000-0000-0000-0000-0000000000a1' $q$) = 0,
        'hr-admin of another tenant cannot mark this tenant''s document superseded');

    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents WHERE superseded_at IS NOT NULL) = 0,
                           'every denied UPDATE left the documents untouched');

    -- hr-admin of the tenant: allowed, and (c) visibility is exactly what it was.
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        $q$ UPDATE documents SET superseded_at = now() WHERE id = 'd0c00000-0000-0000-0000-0000000000a1' $q$) = 1,
        'hr-admin can mark a document superseded');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = v_hr_docs_before,
                           'hr-admin sees the same documents after the mark');

    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = v_emp_docs_before
                           AND (SELECT count(*) FROM chunks) = v_emp_chunks_before,
                           'a superseded document is exactly as visible to an employee as before');
    PERFORM pg_temp.expect(
        (SELECT superseded_at IS NOT NULL FROM documents WHERE id = 'd0c00000-0000-0000-0000-0000000000a1'),
        'the employee can read the supersession mark (it is a ranking signal, not a secret)');

    -- An above-role document stays invisible to the employee whether or not it is marked.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect(pg_temp.rows_affected(
        $q$ UPDATE documents SET superseded_at = now() WHERE id = 'd0c00000-0000-0000-0000-0000000000a3' $q$) = 1,
        'hr-admin can mark a level-3 document superseded');
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'employee');
    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM documents WHERE id = 'd0c00000-0000-0000-0000-0000000000a3'),
        'a superseded level-3 document is still invisible to an employee');
END
$$;

ROLLBACK;
