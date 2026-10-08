-- 07_hnsw_index.sql : RUN AS app_user, after 00_fixture.sql.
-- Smoke only: the index exists with cosine ops, the role-level iterative_scan
-- default applies, and a forced-index ANN query under RLS returns k rows.
-- The fixture is far too small to prove the HNSW under-return fix; the real
-- `returned == k` test on a hostile neighbour distribution is EOGHAN's
-- (TESTING.md section 2). UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    PERFORM pg_temp.expect(
        EXISTS (SELECT 1 FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'chunks'
                  AND indexdef ILIKE '%USING hnsw%' AND indexdef ILIKE '%vector_cosine_ops%'),
        'HNSW cosine index exists on chunks.embedding');

    PERFORM '[1,2,3]'::vector;  -- make sure the vector library (and its GUCs) is loaded
    PERFORM pg_temp.expect(current_setting('hnsw.iterative_scan', true) = 'strict_order',
                           'app_user default hnsw.iterative_scan = strict_order');
END
$$;

SET LOCAL enable_seqscan = off;   -- push the planner toward the index path

DO $$
BEGIN
    -- B's rows are the exact nearest neighbours of dimension 1; A must still get k.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect(
        (SELECT count(*) FROM (
            SELECT id FROM chunks
            ORDER BY embedding <=> (SELECT array_agg(CASE WHEN g = 1 THEN 1::real ELSE 0::real END ORDER BY g)::vector(768)
                                    FROM generate_series(1, 768) g)
            LIMIT 3) q) = 3,
        'forced index path: tenant A gets k=3 rows although tenant B dominates the neighbourhood');
END
$$;

ROLLBACK;
