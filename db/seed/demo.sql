-- Demo identities for running the service locally: 2 tenants x 3 roles.
-- Tenants and users only. Documents are ingested through the API (POST /ingest as hr-admin),
-- so the demo exercises the real write path and its RLS write policy.
--
-- Run as the owner (it writes tenants/users, which app_user cannot). Idempotent.
-- NOT a migration: it lives outside db/migrations so the container never auto-loads demo users.
--
-- Emails are placeholders on the reserved .example TLD. The dev token issuer mints a token for
-- any of them by email alone, which is why that issuer only runs in Development.

INSERT INTO tenants (slug, name) VALUES
    ('acme',   'Acme Ltd (demo)'),
    ('globex', 'Globex Ltd (demo)')
ON CONFLICT (slug) DO NOTHING;

INSERT INTO users (tenant_id, email, role)
SELECT t.id, r.role || '@' || t.slug || '.example', r.role
FROM tenants t
CROSS JOIN (VALUES ('employee'), ('manager'), ('hr-admin')) AS r(role)
WHERE t.slug IN ('acme', 'globex')
ON CONFLICT (tenant_id, email) DO NOTHING;
