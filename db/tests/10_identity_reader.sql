-- 10_identity_reader.sql : RUN AS identity_reader (IDENTITY_URL), after 00_fixture.sql.
-- Proves the login-lookup role can do exactly one thing. UNVERIFIED: never executed.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

-- ---- 1. who we are ---------------------------------------------------------
DO $$
DECLARE r pg_roles%ROWTYPE;
BEGIN
    SELECT * INTO r FROM pg_roles WHERE rolname = current_user;
    PERFORM pg_temp.expect(current_user = 'identity_reader', 'must run as identity_reader, got ' || current_user);
    PERFORM pg_temp.expect(NOT r.rolsuper,      'identity_reader must be NOSUPERUSER');
    PERFORM pg_temp.expect(NOT r.rolbypassrls,  'identity_reader must be NOBYPASSRLS');
    PERFORM pg_temp.expect(NOT r.rolinherit,    'identity_reader must be NOINHERIT');
    PERFORM pg_temp.expect(NOT r.rolcreatedb,   'identity_reader must be NOCREATEDB');
    PERFORM pg_temp.expect(NOT r.rolcreaterole, 'identity_reader must be NOCREATEROLE');
    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = r.oid),
        'identity_reader must be a member of no role');
    PERFORM pg_temp.expect(
        NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relowner = r.oid)
        -- Scoped to public: _helpers.sql creates pg_temp.expect/denied as this role.
        AND NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace
                        WHERE pn.nspname = 'public' AND p.proowner = r.oid),
        'identity_reader must own nothing');
END
$$;

-- ---- 2. no table access of any kind ---------------------------------------
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['roles','tenants','users','documents','chunks','audit_log'] LOOP
        PERFORM pg_temp.expect(
            NOT has_table_privilege('public.' || t, 'SELECT')
            AND NOT has_table_privilege('public.' || t, 'INSERT')
            AND NOT has_table_privilege('public.' || t, 'UPDATE')
            AND NOT has_table_privilege('public.' || t, 'DELETE')
            AND NOT has_table_privilege('public.' || t, 'TRUNCATE')
            AND NOT has_any_column_privilege('public.' || t, 'SELECT'),
            'identity_reader must hold no privilege on ' || t);
        PERFORM pg_temp.expect(
            pg_temp.denied('SELECT count(*) FROM public.' || t),
            'SELECT from ' || t || ' must be permission denied');
    END LOOP;

    -- A tenant context must not unlock anything either.
    -- (set_config directly: pg_temp.as_caller calls rag_role_level, which this role
    -- is rightly not allowed to run.)
    PERFORM set_config('app.tenant_id',  pg_temp.tenant_a()::text, true);
    PERFORM set_config('app.role_level', '3', true);
    PERFORM pg_temp.expect(pg_temp.denied('SELECT * FROM public.users'),
        'setting app.tenant_id must not unlock users');
    PERFORM pg_temp.expect(pg_temp.denied('SELECT * FROM public.tenants'),
        'setting app.tenant_id must not unlock tenants');
    PERFORM pg_temp.expect(
        pg_temp.denied($q$INSERT INTO public.audit_log (tenant_id,user_id,role,query_hash,model,latency_ms)
                          VALUES (gen_random_uuid(),gen_random_uuid(),'employee','x','m',1)$q$),
        'INSERT into audit_log must be denied');
END
$$;

-- ---- 3. cannot call or become anything else --------------------------------
DO $$
BEGIN
    -- Table-reading helper: invoker rights, so it hits roles and is refused.
    PERFORM pg_temp.expect(pg_temp.denied($q$SELECT public.rag_role_level('employee')$q$),
        'rag_role_level must be denied (it reads roles)');
    PERFORM pg_temp.expect(pg_temp.denied('SET ROLE app_user'),             'SET ROLE app_user must be denied');
    PERFORM pg_temp.expect(pg_temp.denied('SET ROLE rag_identity_definer'), 'SET ROLE definer must be denied');
    PERFORM pg_temp.expect(pg_temp.denied('CREATE TABLE public.idr_probe (x int)'),
        'CREATE TABLE in public must be denied');
    PERFORM pg_temp.expect(
        pg_temp.denied($q$CREATE FUNCTION public.idr_probe() RETURNS int LANGUAGE sql AS 'SELECT 1'$q$),
        'CREATE FUNCTION in public must be denied');
END
$$;

-- ---- 4. the function itself is built the way we claim ----------------------
DO $$
DECLARE
    f   pg_proc%ROWTYPE;
    own pg_roles%ROWTYPE;
BEGIN
    SELECT * INTO f FROM pg_proc WHERE oid = 'public.rag_resolve_user(text,text)'::regprocedure;
    SELECT * INTO own FROM pg_roles WHERE oid = f.proowner;

    PERFORM pg_temp.expect(f.prosecdef,   'rag_resolve_user must be SECURITY DEFINER');
    PERFORM pg_temp.expect(f.proisstrict, 'rag_resolve_user must be STRICT');
    PERFORM pg_temp.expect(f.proconfig IS NOT NULL
                           AND 'search_path=pg_catalog, pg_temp' = ANY (f.proconfig),
                           'search_path must be pinned to pg_catalog, pg_temp');
    PERFORM pg_temp.expect(own.rolname = 'rag_identity_definer',
                           'owner must be rag_identity_definer, got ' || own.rolname);
    PERFORM pg_temp.expect(NOT own.rolsuper AND own.rolbypassrls AND NOT own.rolcanlogin,
                           'definer must be NOLOGIN, NOSUPERUSER, BYPASSRLS');
    -- Exactly (p_slug, p_email) in, (user_id, tenant_id, role) out. Nothing else leaks.
    PERFORM pg_temp.expect(
        f.proargnames = ARRAY['p_slug','p_email','user_id','tenant_id','role']
        AND f.proargmodes = ARRAY['i','i','t','t','t']::"char"[],
        'result shape must be exactly (user_id, tenant_id, role)');
    -- ACL: no PUBLIC entry; grantees are only the definer (owner) and identity_reader.
    PERFORM pg_temp.expect(f.proacl IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM aclexplode(f.proacl) a WHERE a.grantee = 0),
        'EXECUTE must be revoked from PUBLIC');
    PERFORM pg_temp.expect(
        (SELECT array_agg(DISTINCT pg_get_userbyid(a.grantee)::text
                          ORDER BY pg_get_userbyid(a.grantee)::text)
           FROM aclexplode(f.proacl) a)
        = ARRAY['identity_reader','rag_identity_definer'],
        'only identity_reader (and the owner) may hold EXECUTE');
    PERFORM pg_temp.expect(NOT has_function_privilege('app_user', f.oid, 'EXECUTE'),
        'app_user must not hold EXECUTE');
END
$$;

-- ---- 5. it resolves seeded users -------------------------------------------
DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM public.rag_resolve_user('tenant-a', 'mgr@a.test');
    PERFORM pg_temp.expect(r.user_id = 'a0000000-0000-0000-0000-000000000002'
                           AND r.tenant_id = pg_temp.tenant_a() AND r.role = 'manager',
                           'tenant-a / mgr@a.test must resolve to the seeded manager');
    SELECT * INTO r FROM public.rag_resolve_user('tenant-b', 'hr@b.test');
    PERFORM pg_temp.expect(r.user_id = 'b0000000-0000-0000-0000-000000000003'
                           AND r.tenant_id = pg_temp.tenant_b() AND r.role = 'hr-admin',
                           'tenant-b / hr@b.test must resolve to the seeded hr-admin');
    PERFORM pg_temp.expect((SELECT count(*) FROM public.rag_resolve_user('tenant-a', 'emp@a.test')) = 1,
                           'a hit returns exactly one row');
END
$$;

-- ---- 6. everything else is ZERO rows (unknown tenant looks the same as unknown user)
DO $$
DECLARE
    cases text[][] := ARRAY[
        ['no-such-tenant', 'mgr@a.test'],    -- unknown tenant, real email elsewhere
        ['no-such-tenant', 'nobody@x.test'], -- unknown tenant, unknown user
        ['tenant-a',       'nobody@a.test'], -- known tenant, unknown user
        ['tenant-a',       'hr@b.test'],     -- other tenant's user under this slug
        ['tenant-b',       'mgr@a.test'],    -- ...and the other way round
        ['tenant-a',       'MGR@A.TEST'],    -- exact match only: no case folding
        ['tenant-a',       ' mgr@a.test'],   -- no trimming
        ['TENANT-A',       'mgr@a.test'],    -- slug is exact too
        ['tenant-a',       '%'],             -- no LIKE wildcards
        ['tenant-a',       '%@a.test'],
        ['%',              'mgr@a.test'],
        ['tenant-',        'mgr@a.test'],    -- no prefix match
        ['tenant-a',       ''],
        ['',               '']
    ];
    i int;
BEGIN
    FOR i IN 1 .. array_length(cases, 1) LOOP
        PERFORM pg_temp.expect(
            (SELECT count(*) FROM public.rag_resolve_user(cases[i][1], cases[i][2])) = 0,
            format('(%L, %L) must return zero rows', cases[i][1], cases[i][2]));
    END LOOP;
    -- STRICT: NULLs give zero rows, not an error.
    PERFORM pg_temp.expect((SELECT count(*) FROM public.rag_resolve_user(NULL, 'mgr@a.test')) = 0, 'NULL slug => 0 rows');
    PERFORM pg_temp.expect((SELECT count(*) FROM public.rag_resolve_user('tenant-a', NULL)) = 0,   'NULL email => 0 rows');
END
$$;

-- ---- 7. a hostile search_path / temp table cannot redirect the lookup ------
CREATE TEMP TABLE tenants (id uuid, slug text);
CREATE TEMP TABLE users   (id uuid, tenant_id uuid, email text, role text);
INSERT INTO pg_temp.tenants VALUES (pg_temp.tenant_a(), 'evil');
INSERT INTO pg_temp.users   VALUES (gen_random_uuid(), pg_temp.tenant_a(), 'x@evil.test', 'hr-admin');
SET LOCAL search_path = pg_temp, public;
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM public.rag_resolve_user('evil', 'x@evil.test')) = 0,
        'temp-table shadowing must not feed the lookup');
    PERFORM pg_temp.expect((SELECT count(*) FROM public.rag_resolve_user('tenant-a', 'mgr@a.test')) = 1,
        'real data must still resolve under a hostile search_path');
END
$$;

ROLLBACK;
