-- 005_write_policies.sql
-- Runs as the owner/superuser after 001/002/004 (CI applies db/migrations/*.sql in
-- filename order). UNVERIFIED: never executed against a real Postgres.
--
-- Fixes two defects found by EOGHAN's step-12 tests (Findings 12-A and 12-B):
--
--   002_rls.sql gave documents and chunks ONE `FOR ALL` policy: tenant AND
--   rag_level_allows(caller_level, required_level), for USING and WITH CHECK. It
--   never looked at WHO the caller is, so any role that could READ a document could
--   also DELETE it (cascading its chunks), rename it, INSERT one at its own level,
--   and a manager could lower a level-2 document to 1 (exposing it to every employee
--   in the tenant) or raise a level-1 document to 2.
--
-- Intended rule (TRD section 1: the tenant admin ingests documents and sets access;
-- the API already gates /ingest and /audit to hr-admin):
--   READ  documents/chunks : tenant AND level_allows(caller, required)      (unchanged)
--   WRITE documents/chunks : tenant AND caller is hr-admin level AND level_allows(...)
--
-- Design:
--   * 002 is NOT edited (already applied and proven in CI). This file drops the two
--     FOR ALL policies and replaces each with four per-command policies. Exactly ONE
--     permissive policy applies to every (table, command), so there is still nothing
--     for Postgres to OR together (TRD 7.4).
--   * The write threshold is rag_can_write(), built from the SAME helpers
--     (rag_current_level, rag_level_allows, rag_role_level), so "hr-admin" is defined
--     once, in the roles table, and is not a magic 3 in a policy.
--   * Fail-closed is inherited: unset context => rag_current_level() NULL =>
--     rag_level_allows(NULL, x) = false => no row passes; an unknown role is the same.
--   * FORCE ROW LEVEL SECURITY from 002 is untouched (policies, not table flags).
--     app_user stays a non-owner; grants are unchanged (the API ingest path still
--     holds SELECT/INSERT/UPDATE/DELETE, RLS now decides who may use them).
--   * Denial shapes, which callers/tests rely on:
--       INSERT                     -> ERROR 42501 "new row violates row-level security policy"
--       UPDATE / DELETE by a role  -> the row fails USING, so it is silently NOT matched:
--                                     0 rows affected, no error (standard RLS behaviour)
--       UPDATE of tenant_id (move) -> USING passes on the old row, WITH CHECK fails on the
--                                     new one: 42501 (RLS), raised before any FK check
--   * ON CONFLICT DO UPDATE (the ingest upsert) needs the SELECT, INSERT and UPDATE
--     policies to pass, which they do for hr-admin in its own tenant.
--   * Chunk cascade on document delete is done by the FK's internal trigger, which does
--     not go through RLS; it is reachable only after the documents DELETE policy
--     (hr-admin only) has let the delete happen.
--
-- Existing data: no row is touched. Behaviour changes for non-hr-admin writers only.
-- Reverse with: db/down/005_write_policies.down.sql

-- ---------------------------------------------------------------------------
-- 1. The single definition of "may write documents/chunks".
--    hr-admin's level is looked up in roles, never hard-coded. NULL (no context,
--    unknown role, or no such role row) => false.
-- ---------------------------------------------------------------------------
CREATE FUNCTION rag_can_write()
RETURNS boolean
LANGUAGE sql STABLE
AS $$
    SELECT public.rag_level_allows(public.rag_current_level(), public.rag_role_level('hr-admin'))
$$;

-- ---------------------------------------------------------------------------
-- 2. documents
-- ---------------------------------------------------------------------------
DROP POLICY tenant_role_isolation ON documents;

CREATE POLICY documents_select ON documents
    FOR SELECT
    USING (
        tenant_id = rag_current_tenant()
        AND rag_level_allows(rag_current_level(), required_level)
    );

CREATE POLICY documents_insert ON documents
    FOR INSERT
    WITH CHECK (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND rag_level_allows(rag_current_level(), required_level)
    );

CREATE POLICY documents_update ON documents
    FOR UPDATE
    USING (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND rag_level_allows(rag_current_level(), required_level)
    )
    WITH CHECK (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND rag_level_allows(rag_current_level(), required_level)
    );

CREATE POLICY documents_delete ON documents
    FOR DELETE
    USING (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND rag_level_allows(rag_current_level(), required_level)
    );

-- ---------------------------------------------------------------------------
-- 3. chunks: the ACL is still read from the PARENT document, never copied. The
--    EXISTS subquery on documents is itself subject to documents_select.
-- ---------------------------------------------------------------------------
DROP POLICY tenant_role_isolation ON chunks;

CREATE POLICY chunks_select ON chunks
    FOR SELECT
    USING (
        tenant_id = rag_current_tenant()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    );

CREATE POLICY chunks_insert ON chunks
    FOR INSERT
    WITH CHECK (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    );

CREATE POLICY chunks_update ON chunks
    FOR UPDATE
    USING (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
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
        AND rag_can_write()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    );

CREATE POLICY chunks_delete ON chunks
    FOR DELETE
    USING (
        tenant_id = rag_current_tenant()
        AND rag_can_write()
        AND EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.id = chunks.doc_id
              AND d.tenant_id = chunks.tenant_id
              AND rag_level_allows(rag_current_level(), d.required_level)
        )
    );

-- ---------------------------------------------------------------------------
-- 4. Make "raw query text is never stored" structural (EOGHAN hardening note).
--    audit_log.query_text exists in 001 as an optional column. The service never
--    writes it (NpgsqlChunkStore.AppendAuditAsync omits it; only query_hash is
--    stored), but app_user held a table-level INSERT, so a bug or compromised
--    request path could have filled it. A CHECK makes any non-NULL value impossible
--    for EVERY role (23514), not just app_user, and keeps the table-level INSERT
--    grant (and has_table_privilege('INSERT')) exactly as it was.
--    The column is kept (dropping it would be a schema change the app does not need).
--    Existing data: this FAILS LOUDLY if any row already has query_text set. Check
--    first:  SELECT count(*) FROM audit_log WHERE query_text IS NOT NULL;
-- ---------------------------------------------------------------------------
ALTER TABLE audit_log
    ADD CONSTRAINT audit_log_query_text_never_stored CHECK (query_text IS NULL);

COMMENT ON COLUMN audit_log.query_text IS
    'Reserved and always NULL (CHECK audit_log_query_text_never_stored). Only query_hash is stored.';
