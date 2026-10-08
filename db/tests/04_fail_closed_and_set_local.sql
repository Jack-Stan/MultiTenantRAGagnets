-- 04_fail_closed_and_set_local.sql : RUN AS app_user, after 00_fixture.sql.
-- Unset / half-set / ended-transaction context returns 0 rows: not an error and
-- not another tenant's rows (TRD 7.2). UNVERIFIED.
\set ON_ERROR_STOP on

-- (a) Nothing set at all (fresh session).
BEGIN;
\ir _helpers.sql
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks)    = 0, 'unset context: 0 chunks');
    PERFORM pg_temp.expect((SELECT count(*) FROM documents) = 0, 'unset context: 0 documents');
    PERFORM pg_temp.expect((SELECT count(*) FROM tenants)   = 0, 'unset context: 0 tenants');
END
$$;
ROLLBACK;

-- (b) Tenant set but role level missing, and vice versa.
BEGIN;
\ir _helpers.sql
SELECT set_config('app.tenant_id', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', true);
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 0, 'tenant without role: 0 chunks');
END
$$;
ROLLBACK;

BEGIN;
\ir _helpers.sql
SELECT set_config('app.role_level', '3', true);
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 0, 'role without tenant: 0 chunks');
END
$$;
ROLLBACK;

-- (c) SET LOCAL must not outlive its transaction on a REUSED connection.
--     This COMMITs (it only sets a transaction-local GUC; no data changes).
BEGIN;
\ir _helpers.sql
SELECT pg_temp.as_caller('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'::uuid, 'hr-admin');
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 4, 'inside txn: A hr-admin sees 4 chunks');
END
$$;
COMMIT;

-- Same session, next "request" without re-setting: the leftover '' value must
-- fail closed. This is the pooled-connection regression detector.
BEGIN;
\ir _helpers.sql
DO $$
BEGIN
    PERFORM pg_temp.expect((SELECT count(*) FROM chunks) = 0,
                           'after COMMIT, reused connection sees 0 chunks (SET LOCAL did not leak)');
    PERFORM pg_temp.expect(coalesce(current_setting('app.tenant_id', true), '') = '',
                           'app.tenant_id is empty after the txn ended');
END
$$;
ROLLBACK;
