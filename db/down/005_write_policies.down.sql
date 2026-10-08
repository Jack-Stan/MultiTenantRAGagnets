-- 005_write_policies.down.sql : reverses db/migrations/005_write_policies.sql.
-- Restores the 002 state: ONE `FOR ALL` policy on documents and chunks, and
-- drops the query_text CHECK. WARNING: this re-opens Findings 12-A/12-B (lower
-- roles can write documents/chunks at the database layer). Dev databases only.
-- Existing data: no row is touched.
-- Run as the owner/superuser, BEFORE the 004/002/001 down scripts (002.down drops
-- rag_current_*, which these policies depend on). UNVERIFIED.

ALTER TABLE audit_log DROP CONSTRAINT IF EXISTS audit_log_query_text_never_stored;
COMMENT ON COLUMN audit_log.query_text IS NULL;

DROP POLICY IF EXISTS documents_select ON documents;
DROP POLICY IF EXISTS documents_insert ON documents;
DROP POLICY IF EXISTS documents_update ON documents;
DROP POLICY IF EXISTS documents_delete ON documents;
DROP POLICY IF EXISTS chunks_select ON chunks;
DROP POLICY IF EXISTS chunks_insert ON chunks;
DROP POLICY IF EXISTS chunks_update ON chunks;
DROP POLICY IF EXISTS chunks_delete ON chunks;

-- Verbatim from 002_rls.sql.
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

DROP FUNCTION IF EXISTS rag_can_write();
