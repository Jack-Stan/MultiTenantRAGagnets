-- 006_document_supersession.sql
-- Runs as the owner/superuser after 005 (CI and docker-compose init apply db/migrations/*.sql in
-- filename order). UNVERIFIED until the first CI run that applies it.
--
-- Why: the corpus (and any real policy library) holds several editions of the same document as
-- SEPARATE documents. The embedding model does not tell a withdrawn edition from the current one
-- (measured: near-duplicate MRR@5 about 0.50 with nomic-embed-text), and the schema had no
-- "this document has been replaced" signal, only `version`, which is the revision counter of ONE
-- document and is not set by a replacement. The retrieval layer needs a signal that is not the
-- text itself.
--
-- Design:
--   * One nullable column on documents. NULL = current. A timestamp = withdrawn at that time.
--     No default, no backfill: every existing row stays "current", exactly as it behaves today.
--   * It is a document attribute joined at query time, like required_level. Nothing is copied to
--     chunks, so withdrawing an edition needs no re-embed.
--   * It is a RANKING signal only. It never appears in an RLS policy or in the visibility
--     predicate: a superseded document is exactly as visible as before to exactly the same callers.
--   * Privileges: NOT a new table, and no new GRANT is needed. app_user already holds table-level
--     SELECT/INSERT/UPDATE/DELETE on documents (002), which covers a new column. WHO may set it is
--     decided by the 005 write policies unchanged (hr-admin level of the tenant only; any other
--     role matches 0 rows on UPDATE and gets 42501 on INSERT). app_user stays non-owner and
--     NOBYPASSRLS; FORCE ROW LEVEL SECURITY is untouched.
--
-- Reverse with: db/down/006_document_supersession.down.sql

ALTER TABLE documents ADD COLUMN superseded_at timestamptz NULL;

COMMENT ON COLUMN documents.superseded_at IS
    'NULL = current edition. Non-NULL = withdrawn/replaced at that time. Ranking signal only: never part of any RLS policy or visibility predicate.';
