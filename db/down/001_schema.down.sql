-- 001_schema.down.sql : reverses db/migrations/001_schema.sql.
-- DESTRUCTIVE: drops every table and all data in them (tenants, users,
-- documents, chunks, audit_log). Local dev database only, and only with
-- Stan's explicit yes for the run. Run 002_rls.down.sql FIRST.
-- Run as the owner/superuser. UNVERIFIED.

DROP TABLE IF EXISTS audit_log;
DROP TABLE IF EXISTS chunks;      -- takes the HNSW index with it
DROP TABLE IF EXISTS documents;
DROP TABLE IF EXISTS users;
DROP TABLE IF EXISTS tenants;

DROP FUNCTION IF EXISTS rag_level_allows(integer, integer);
DROP FUNCTION IF EXISTS rag_role_level(text);
DROP TABLE IF EXISTS roles;

-- The vector extension is deliberately LEFT in place: other objects may use it
-- and dropping it is not part of undoing this schema. To remove it too:
--   DROP EXTENSION IF EXISTS vector;
