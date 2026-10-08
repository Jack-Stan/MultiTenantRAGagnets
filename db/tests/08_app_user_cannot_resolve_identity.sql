-- 08_app_user_cannot_resolve_identity.sql : RUN AS app_user.
-- The login lookup is identity_reader's alone. app_user is the tenant-scoped,
-- post-authentication role; letting it resolve arbitrary (slug, email) would let
-- a compromised request path probe every tenant's membership. UNVERIFIED.
\set ON_ERROR_STOP on
BEGIN;
\ir _helpers.sql

DO $$
BEGIN
    PERFORM pg_temp.expect(current_user = 'app_user', 'must run as app_user, got ' || current_user);
    PERFORM pg_temp.expect(
        NOT has_function_privilege('public.rag_resolve_user(text,text)', 'EXECUTE'),
        'app_user must not hold EXECUTE on rag_resolve_user');
    PERFORM pg_temp.expect(
        pg_temp.denied($q$SELECT * FROM public.rag_resolve_user('tenant-a','mgr@a.test')$q$),
        'app_user calling rag_resolve_user must be permission denied');

    -- Setting a tenant context must not change that.
    PERFORM pg_temp.as_caller(pg_temp.tenant_a(), 'hr-admin');
    PERFORM pg_temp.expect(
        pg_temp.denied($q$SELECT * FROM public.rag_resolve_user('tenant-b','hr@b.test')$q$),
        'app_user with a tenant context must still be denied');
END
$$;

ROLLBACK;
