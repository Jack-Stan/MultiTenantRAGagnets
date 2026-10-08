-- 004_identity_role.sql
-- Runs as the owner/superuser after 001/002 (CI applies db/migrations/*.sql in
-- filename order). UNVERIFIED: never executed against a real Postgres.
--
-- Problem: login (tenant slug + email -> user id, tenant id, role) happens BEFORE
-- any tenant context exists, but tenants/users are tenant-scoped under FORCE RLS.
-- Solution: ONE SECURITY DEFINER function, rag_resolve_user(slug, email), and a
-- login role (identity_reader) that can execute it and do nothing else. No table
-- grants to identity_reader at all, so nothing can be read around the function.
--
-- Reverse with: db/down/004_identity_role.down.sql
-- Password: 004_identity_user_password.sh (sorts AFTER this file on purpose:
-- 'r' < 'u'. Do not rename it to anything sorting before 004_identity_role.sql).

-- ---------------------------------------------------------------------------
-- 1. The definer: NOLOGIN, owns only the function. BYPASSRLS so the function can
--    see across tenants even if the schema owner were a non-superuser. Only
--    COLUMN-level SELECT on the four columns the lookup needs, so even if the
--    function body were edited it could not read anything else.
--    (Deliberately NOT the schema owner/superuser: a superuser-owned definer
--    function is the most powerful thing in the database.)
--    Creating a BYPASSRLS role needs superuser, as the migration runner is.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rag_identity_definer') THEN
        CREATE ROLE rag_identity_definer NOLOGIN;
    END IF;
END
$$;

ALTER ROLE rag_identity_definer WITH NOLOGIN NOSUPERUSER BYPASSRLS NOCREATEDB
                                     NOCREATEROLE NOREPLICATION NOINHERIT;

GRANT USAGE ON SCHEMA public TO rag_identity_definer;
GRANT SELECT (id, slug)                   ON public.tenants TO rag_identity_definer;
GRANT SELECT (id, tenant_id, email, role) ON public.users   TO rag_identity_definer;

-- ---------------------------------------------------------------------------
-- 2. The login-lookup role. No password here (secrets guard):
--    004_identity_user_password.sh sets it from IDENTITY_USER_PASSWORD.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'identity_reader') THEN
        CREATE ROLE identity_reader LOGIN;
    END IF;
END
$$;

ALTER ROLE identity_reader WITH LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE
                                NOREPLICATION NOINHERIT CONNECTION LIMIT 10;
-- Cheap guard against the lookup being used as a slow probe.
ALTER ROLE identity_reader SET statement_timeout = '2s';

-- Belt and braces: it must hold no table privileges at all.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM identity_reader;
GRANT USAGE ON SCHEMA public TO identity_reader;   -- needed to name the function

-- ---------------------------------------------------------------------------
-- 3. The function.
--    * SECURITY DEFINER, owner = rag_identity_definer (BYPASSRLS, column grants only).
--    * search_path pinned to pg_catalog, pg_temp and every object schema-qualified,
--      so neither a hostile search_path nor a temp table can redirect it.
--    * Exact equality only: no LIKE, no lower(), no prefix. Callers normalise.
--    * STRICT: NULL slug/email => zero rows.
--    * At most one row (slug and (tenant_id,email) are UNIQUE; LIMIT 1 as a backstop).
--    * Unknown tenant and unknown user are indistinguishable: both are ZERO rows.
--    * Returns exactly (user_id, tenant_id, role). No email, no tenant name, no created_at.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.rag_resolve_user(p_slug text, p_email text)
RETURNS TABLE (user_id uuid, tenant_id uuid, role text)
LANGUAGE sql STABLE STRICT SECURITY DEFINER
ROWS 1
SET search_path = pg_catalog, pg_temp
AS $$
    SELECT u.id, u.tenant_id, u.role
    FROM public.tenants t
    JOIN public.users   u ON u.tenant_id = t.id
    WHERE t.slug = p_slug
      AND u.email = p_email
    LIMIT 1
$$;

ALTER FUNCTION public.rag_resolve_user(text, text) OWNER TO rag_identity_definer;

-- New functions get EXECUTE for PUBLIC by default: take it away, then grant narrowly.
-- app_user is deliberately NOT granted: it is the post-authentication, tenant-scoped
-- role. If it could resolve any (slug, email), a compromised request path could probe
-- membership of every tenant, which is exactly what RLS is there to prevent.
REVOKE ALL ON FUNCTION public.rag_resolve_user(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.rag_resolve_user(text, text) FROM app_user;
GRANT EXECUTE ON FUNCTION public.rag_resolve_user(text, text) TO identity_reader;
