-- 006_document_supersession.down.sql : reverses db/migrations/006_document_supersession.sql.
-- Drops the column and with it every supersession mark. Run BEFORE the 005 down script.
-- Dev databases only.
ALTER TABLE documents DROP COLUMN IF EXISTS superseded_at;
