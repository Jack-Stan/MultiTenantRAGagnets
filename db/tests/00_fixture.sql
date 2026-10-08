-- 00_fixture.sql : RUN AS THE OWNER/SUPERUSER (not app_user), once, before the
-- other tests. Idempotent. A superuser bypasses RLS; a non-superuser owner would
-- be blocked by FORCE RLS here. UNVERIFIED: never executed.
--
-- Shape (2 tenants, 3 levels):
--   A: doc a1 (L1, 2 chunks), a2 (L2, 1 chunk), a3 (L3, 1 chunk)
--   B: doc b1 (L1, 1 chunk),  b3 (L3, 1 chunk)
--   => A sees employee 2 / manager 3 / hr-admin 4 chunks; B sees employee 1 / hr-admin 2.
-- Embeddings are one-hot vectors; B's chunks sit on dimension 1 so a query on
-- dimension 1 has the OTHER tenant's rows as its nearest neighbours.
\set ON_ERROR_STOP on
BEGIN;

CREATE FUNCTION pg_temp.onehot(n integer) RETURNS vector LANGUAGE sql AS $$
    SELECT (array_agg(CASE WHEN g = n THEN 1::real ELSE 0::real END ORDER BY g))::vector(768)
    FROM generate_series(1, 768) AS g
$$;

INSERT INTO tenants (id, slug, name) VALUES
    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'tenant-a', 'Tenant A'),
    ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'tenant-b', 'Tenant B')
ON CONFLICT DO NOTHING;

INSERT INTO users (id, tenant_id, email, role) VALUES
    ('a0000000-0000-0000-0000-000000000001', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'emp@a.test', 'employee'),
    ('a0000000-0000-0000-0000-000000000002', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'mgr@a.test', 'manager'),
    ('a0000000-0000-0000-0000-000000000003', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'hr@a.test',  'hr-admin'),
    ('b0000000-0000-0000-0000-000000000001', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'emp@b.test', 'employee'),
    ('b0000000-0000-0000-0000-000000000003', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'hr@b.test',  'hr-admin')
ON CONFLICT DO NOTHING;

INSERT INTO documents (id, tenant_id, title, required_level) VALUES
    ('d0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'A handbook',     1),
    ('d0c00000-0000-0000-0000-0000000000a2', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'A manager memo',  2),
    ('d0c00000-0000-0000-0000-0000000000a3', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'A salary bands',  3),
    ('d0c00000-0000-0000-0000-0000000000b1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'B handbook',     1),
    ('d0c00000-0000-0000-0000-0000000000b3', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'B salary bands',  3)
ON CONFLICT DO NOTHING;

INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding) VALUES
    ('c0000000-0000-0000-0000-0000000000a1', 'd0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0, 'A handbook part 1', pg_temp.onehot(2)),
    ('c0000000-0000-0000-0000-0000000000a2', 'd0c00000-0000-0000-0000-0000000000a1', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 1, 'A handbook part 2', pg_temp.onehot(3)),
    ('c0000000-0000-0000-0000-0000000000a3', 'd0c00000-0000-0000-0000-0000000000a2', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0, 'A manager memo',    pg_temp.onehot(4)),
    ('c0000000-0000-0000-0000-0000000000a4', 'd0c00000-0000-0000-0000-0000000000a3', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0, 'A salary bands',   pg_temp.onehot(5)),
    ('c0000000-0000-0000-0000-0000000000b1', 'd0c00000-0000-0000-0000-0000000000b1', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 0, 'B handbook',       pg_temp.onehot(1)),
    ('c0000000-0000-0000-0000-0000000000b2', 'd0c00000-0000-0000-0000-0000000000b3', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 0, 'B salary bands',    pg_temp.onehot(1))
ON CONFLICT DO NOTHING;

COMMIT;
