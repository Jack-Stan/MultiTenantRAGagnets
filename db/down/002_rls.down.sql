-- 002_rls.down.sql : reverses db/migrations/002_rls.sql.
-- Existing data: none is touched, but after this runs the tables are NOT
-- row-protected. Dev databases only.
-- Run as the owner/superuser. UNVERIFIED.

DROP POLICY IF EXISTS tenant_isolation      ON tenants;
DROP POLICY IF EXISTS tenant_isolation      ON users;
DROP POLICY IF EXISTS tenant_role_isolation ON documents;
DROP POLICY IF EXISTS tenant_role_isolation ON chunks;
DROP POLICY IF EXISTS tenant_isolation      ON audit_log;

ALTER TABLE tenants   NO FORCE ROW LEVEL SECURITY;
ALTER TABLE tenants   DISABLE  ROW LEVEL SECURITY;
ALTER TABLE users     NO FORCE ROW LEVEL SECURITY;
ALTER TABLE users     DISABLE  ROW LEVEL SECURITY;
ALTER TABLE documents NO FORCE ROW LEVEL SECURITY;
ALTER TABLE documents DISABLE  ROW LEVEL SECURITY;
ALTER TABLE chunks    NO FORCE ROW LEVEL SECURITY;
ALTER TABLE chunks    DISABLE  ROW LEVEL SECURITY;
ALTER TABLE audit_log NO FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log DISABLE  ROW LEVEL SECURITY;

DROP FUNCTION IF EXISTS rag_current_level();
DROP FUNCTION IF EXISTS rag_current_tenant();

-- Removes app_user's grants and per-role settings, then the role itself.
-- Fails if app_user owns objects or has live sessions: that is deliberate.
DROP OWNED BY app_user;
DROP ROLE IF EXISTS app_user;
