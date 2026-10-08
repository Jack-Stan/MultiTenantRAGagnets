-- _helpers.sql : temp helpers, included by each test via  \ir _helpers.sql
-- Must be included INSIDE the test's BEGIN. Objects live in pg_temp (session-local)
-- and need only the TEMP privilege that PUBLIC has by default.
-- CREATE OR REPLACE because 04_*.sql COMMITs mid-file and re-includes this.
-- UNVERIFIED: never executed.

-- Become a caller for the rest of the current transaction. set_config(..., true)
-- is the parameterisable form of SET LOCAL (same per-transaction semantics).
-- Level comes from the SAME rag_role_level() the app is told to use.
CREATE OR REPLACE FUNCTION pg_temp.as_caller(p_tenant uuid, p_role text) RETURNS void
LANGUAGE sql AS $$
    SELECT set_config('app.tenant_id',  p_tenant::text, true),
           set_config('app.role_level', coalesce(public.rag_role_level(p_role)::text, ''), true)
$$;

CREATE OR REPLACE FUNCTION pg_temp.expect(p_ok boolean, p_msg text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_ok IS NOT TRUE THEN
        RAISE EXCEPTION 'SMOKE TEST FAILED: %', p_msg;
    END IF;
END
$$;

-- Fixed fixture ids (see 00_fixture.sql).
CREATE OR REPLACE FUNCTION pg_temp.tenant_a() RETURNS uuid LANGUAGE sql IMMUTABLE AS
    $$ SELECT 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'::uuid $$;
CREATE OR REPLACE FUNCTION pg_temp.tenant_b() RETURNS uuid LANGUAGE sql IMMUTABLE AS
    $$ SELECT 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'::uuid $$;

-- True if the statement is rejected for lack of privilege / RLS WITH CHECK
-- (SQLSTATE 42501) or by a foreign key (23503).
CREATE OR REPLACE FUNCTION pg_temp.denied(p_sql text) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE p_sql;
    RETURN false;
EXCEPTION WHEN insufficient_privilege OR foreign_key_violation THEN
    RETURN true;
END
$$;

-- STRICT denial: true ONLY for SQLSTATE 42501 (privilege / RLS WITH CHECK). A foreign
-- key (23503) or any other error is NOT accepted, so a test cannot pass because some
-- unrelated constraint happened to fire first.
CREATE OR REPLACE FUNCTION pg_temp.denied_42501(p_sql text) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE p_sql;
    RETURN false;
EXCEPTION WHEN insufficient_privilege THEN
    RETURN true;
END
$$;

-- Rows touched by a statement. RLS never errors on UPDATE/DELETE of rows the policy
-- hides or forbids: it simply matches 0 rows, so those denials are asserted as 0.
CREATE OR REPLACE FUNCTION pg_temp.rows_affected(p_sql text) RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE n bigint;
BEGIN
    EXECUTE p_sql;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END
$$;

-- Any valid 768-dim embedding (all ones: non-zero, so cosine/HNSW accept it).
CREATE OR REPLACE FUNCTION pg_temp.anyvec() RETURNS vector LANGUAGE sql AS
    $$ SELECT array_fill(1::real, ARRAY[768])::vector(768) $$;
