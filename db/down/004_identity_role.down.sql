-- 004_identity_role.down.sql : reverses db/migrations/004_identity_role.sql.
-- Existing data: no table data is touched. Effect: login lookup stops working
-- (anything using the Identity connection / rag_resolve_user will fail).
-- Run as the owner/superuser, BEFORE the 002/001 down scripts. UNVERIFIED.
-- Fails if identity_reader has live sessions: that is deliberate.

DROP FUNCTION IF EXISTS public.rag_resolve_user(text, text);

DROP OWNED BY identity_reader;           -- its grants; per-role settings die with the role
DROP ROLE IF EXISTS identity_reader;

DROP OWNED BY rag_identity_definer;      -- its column grants on tenants/users
DROP ROLE IF EXISTS rag_identity_definer;
