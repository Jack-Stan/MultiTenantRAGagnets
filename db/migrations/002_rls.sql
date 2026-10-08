-- 002_rls.sql  (IMPLEMENTATION_PLAN step 3; TRD section 7.1, 7.2, 7.4)
-- Runs as the owner/superuser after 001_schema.sql.
-- UNVERIFIED: never executed against a real Postgres.
--
-- Reverse with: db/down/002_rls.down.sql

-- ---------------------------------------------------------------------------
-- 1. Locked-down request-path role (TRD 7.1).
--    No password here (secrets guard): 003_app_user_password.sh sets it from
--    the APP_USER_PASSWORD env var.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        CREATE ROLE app_user LOGIN;
    END IF;
END
$$;

ALTER ROLE app_user WITH LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE
                         NOREPLICATION NOINHERIT;

-- Iterative index scans on by default for the request path (TRD 7.3).
-- The app should still SET LOCAL it per transaction; this is the safety net.
ALTER ROLE app_user SET hnsw.iterative_scan = 'strict_order';

-- ---------------------------------------------------------------------------
-- 2. Reading the per-request context (TRD 7.2).
--    current_setting(name, true) returns NULL if never set, but '' after a
--    SET LOCAL's transaction has ended in the same session. nullif() turns
--    both into NULL, and NULL makes every comparison false => fail closed
--    (0 rows), never an error and never another tenant's rows.
-- ---------------------------------------------------------------------------
CREATE FUNCTION rag_current_tenant()
RETURNS uuid
LANGUAGE sql STABLE
AS $$
    SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

CREATE FUNCTION rag_current_level()
RETURNS integer
LANGUAGE sql STABLE
AS $$
    SELECT nullif(current_setting('app.role_level', true), '')::integer
$$;

-- ---------------------------------------------------------------------------
-- 3. Row level security: ENABLE + FORCE, ONE combined policy per table (TRD 7.4).
--    FOR ALL with explicit USING and WITH CHECK. No second permissive policy
--    on any table, so there is nothing for Postgres to OR together.
-- ---------------------------------------------------------------------------
ALTER TABLE tenants   ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants   FORCE  ROW LEVEL SECURITY;
ALTER TABLE users     ENABLE ROW LEVEL SECURITY;
ALTER TABLE users     FORCE  ROW LEVEL SECURITY;
ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents FORCE  ROW LEVEL SECURITY;
ALTER TABLE chunks    ENABLE ROW LEVEL SECURITY;
ALTER TABLE chunks    FORCE  ROW LEVEL SECURITY;
ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE  ROW LEVEL SECURITY;

-- tenants / users: tenant wall only (no role dimension on these rows).
CREATE POLICY tenant_isolation ON tenants
    FOR ALL
    USING      (id = rag_current_tenant())
    WITH CHECK (id = rag_current_tenant());

CREATE POLICY tenant_isolation ON users
    FOR ALL
    USING      (tenant_id = rag_current_tenant())
    WITH CHECK (tenant_id = rag_current_tenant());

-- documents: tenant AND role level, via the shared hierarchy function.
CREATE POLICY tenant_role_isolation ON documents
    FOR ALL
    USING (
        tenant_id = rag_current_tenant()
        AND rag_level_allows(rag_current_level(), required_level)
    )
    WITH CHECK (
        tenant_id = rag_current_tenant()
        AND rag_level_allows(rag_current_level(), required_level)
    );

-- chunks: tenant AND the PARENT DOCUMENT's level. The ACL is read from
-- documents at query time (never copied onto chunks). The subquery on
-- documents is itself subject to documents' policy.
CREATE POLICY tenant_role_isolation ON chunks
    FOR ALL
    USING (
        tenant_id = rag_current_tenant()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    )
    WITH CHECK (
        tenant_id = rag_current_tenant()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    );

-- audit_log: tenant wall only. (Role-gating who may READ the log is an API
-- concern for /audit; RLS here just stops cross-tenant reads and forged tenant
-- ids on insert.)
CREATE POLICY tenant_isolation ON audit_log
    FOR ALL
    USING      (tenant_id = rag_current_tenant())
    WITH CHECK (tenant_id = rag_current_tenant());

-- ---------------------------------------------------------------------------
-- 4. Grants (least privilege). Tables carry no PUBLIC privileges by default.
-- ---------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM app_user;
GRANT USAGE ON SCHEMA public TO app_user;

-- Reference / identity data: read-only for the request path (seeded by owner).
GRANT SELECT ON roles, tenants, users TO app_user;

-- Ingest + access-change path needs full DML on documents and chunks.
GRANT SELECT, INSERT, UPDATE, DELETE ON documents, chunks TO app_user;

-- APPEND-ONLY: INSERT and SELECT only. No UPDATE, DELETE or TRUNCATE.
-- (id is GENERATED ALWAYS AS IDENTITY, so INSERT needs no sequence grant.)
GRANT SELECT, INSERT ON audit_log TO app_user;
