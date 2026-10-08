-- 01_role_locked_down.sql : RUN AS app_user. Guards the test run itself: if this
-- fails, every later "0 rows" result is meaningless (an owner/superuser sees all).
-- UNVERIFIED: never executed.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
DECLARE r pg_roles%ROWTYPE;
BEGIN
    SELECT * INTO r FROM pg_roles WHERE rolname = current_user;
    PERFORM pg_temp.expect(current_user = 'app_user', 'tests must run as app_user, got ' || current_user);
    PERFORM pg_temp.expect(NOT r.rolsuper,      'app_user must be NOSUPERUSER');
    PERFORM pg_temp.expect(NOT r.rolbypassrls,  'app_user must be NOBYPASSRLS');
    PERFORM pg_temp.expect(NOT r.rolcreatedb,   'app_user must be NOCREATEDB');
    PERFORM pg_temp.expect(NOT r.rolcreaterole, 'app_user must be NOCREATEROLE');

    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM pg_tables
                    WHERE schemaname = 'public' AND tableowner = current_user),
        'app_user must own no tables');

    PERFORM pg_temp.expect(
        (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'public'
            AND c.relname IN ('tenants','users','documents','chunks','audit_log')
            AND c.relrowsecurity AND c.relforcerowsecurity) = 5,
        'all 5 tables must have RLS enabled AND forced');

    -- Exactly ONE permissive policy applies to every (table, command): no OR-widening.
    -- tenants/users/audit_log keep one FOR ALL policy; documents and chunks have separate
    -- SELECT / INSERT / UPDATE / DELETE policies (005_write_policies.sql) and no FOR ALL.
    PERFORM pg_temp.expect(
        NOT EXISTS (
            SELECT 1
            FROM (VALUES ('tenants'), ('users'), ('documents'), ('chunks'), ('audit_log')) t(tbl)
            CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE')) c(cmd)
            WHERE (SELECT count(*) FROM pg_policies p
                    WHERE p.schemaname = 'public' AND p.tablename = t.tbl
                      AND p.cmd IN (c.cmd, 'ALL')) <> 1),
        'every (table, command) of the 5 RLS tables must be covered by exactly one policy');
    PERFORM pg_temp.expect(
        (SELECT count(*) FROM pg_policies WHERE schemaname = 'public') = 11
        AND NOT EXISTS (SELECT 1 FROM pg_policies
                         WHERE schemaname = 'public' AND permissive <> 'PERMISSIVE'),
        'expected 11 policies (1+1+4+4+1), all permissive');
    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM pg_policies
                     WHERE schemaname = 'public' AND tablename IN ('documents', 'chunks') AND cmd = 'ALL'),
        'documents and chunks must not have a FOR ALL policy (reads and writes are separate)');

    -- Reference/identity data is read-only for the request path.
    PERFORM pg_temp.expect(NOT has_table_privilege('roles',   'INSERT'), 'app_user must not INSERT roles');
    PERFORM pg_temp.expect(NOT has_table_privilege('tenants', 'INSERT'), 'app_user must not INSERT tenants');
    PERFORM pg_temp.expect(NOT has_table_privilege('users',   'INSERT'), 'app_user must not INSERT users');
END
$$;

ROLLBACK;
