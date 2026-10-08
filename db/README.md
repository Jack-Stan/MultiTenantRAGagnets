# db/ - schema, RLS, smoke tests

> **UNVERIFIED.** No Postgres, Docker or psql was available when this was written.
> None of this SQL has ever been executed. The first real run against
> `pgvector/pgvector:pg16` is the actual test; treat every file as a draft until
> `db/tests/run.sh` passes there.

Implements IMPLEMENTATION_PLAN steps 2 and 3 (see `docs/TRD.md` section 7).

## Layout and order

| File | Runs as | What |
|------|---------|------|
| `migrations/001_schema.sql` | owner/superuser | extension, `roles` + hierarchy functions, `tenants`, `users`, `documents`, `chunks` (+ HNSW), `audit_log` |
| `migrations/002_rls.sql` | owner/superuser | `app_user` role, RLS enable+force, one policy per table, grants (audit append-only) |
| `migrations/003_app_user_password.sh` | owner/superuser | sets `app_user`'s password from `APP_USER_PASSWORD` (no secret in git) |
| `down/002_rls.down.sql`, `down/001_schema.down.sql` | owner/superuser | reverse, in that order. Destructive: dev only |
| `tests/` | see below | smoke checks |

### How they are applied

Mount `db/migrations` at `/docker-entrypoint-initdb.d` on the Postgres container.
The image runs `*.sql` and `*.sh` there in **alphabetical order, once, only when the
data directory is empty**, as `POSTGRES_USER` (a superuser) against `POSTGRES_DB`.
So `001` -> `002` -> `003`. To re-apply after a change: `docker compose down -v`
(drops the volume) then up. There is no migration runner/versions table yet; if
migrations outgrow this, move to one without changing the file contents.

The container needs `APP_USER_PASSWORD` in its environment (compose, owned by
PADRAIG). The API's request-path connection string uses `app_user`; **never** the
owner.

`down/` sits outside `migrations/` on purpose so the entrypoint never runs it.

## Access model in the database

- `roles(name, level)`: employee=1, manager=2, hr-admin=3. A document is visible to a
  caller when `required_level <= caller_level`, so hr-admin inherits manager and employee.
- ACL lives on `documents.required_level`. `chunks` has **no** ACL column; chunk policy
  and queries join to the parent document. Changing a document's level takes effect on
  the next query, no re-embed. `chunks (doc_id, tenant_id)` is a composite FK to
  `documents (id, tenant_id)` so a chunk cannot carry a different tenant than its document.
- **One expansion, two callers.** `rag_level_allows(caller_level, required_level)` is
  used by the RLS policies *and* must be used by the app filter. `rag_role_level(role_name)`
  maps a JWT role to the level.

### App-layer query (must use the same functions)

```sql
SELECT c.id, c.text, c.embedding <=> @q AS distance
FROM chunks c
JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
WHERE c.tenant_id = @tenant
  AND rag_level_allows(@caller_level, d.required_level)
ORDER BY c.embedding <=> @q
LIMIT @k;
```

For the RLS-only harness run, drop the `WHERE` and the join and keep just
`SELECT ... FROM chunks ORDER BY embedding <=> @q LIMIT @k` as `app_user`.

## Per-request pattern (SET LOCAL, never a session SET)

Every request, on a connection authenticated as `app_user`, in ONE transaction:

```sql
BEGIN;
SELECT set_config('app.tenant_id',  @tenant_id, true),                         -- true = SET LOCAL
       set_config('app.role_level', coalesce(rag_role_level(@role)::text, ''), true);
SET LOCAL hnsw.iterative_scan = 'strict_order';   -- also the role default (002)
-- ... retrieval query, audit_log INSERT ...
COMMIT;
```

- `set_config(name, value, true)` is exactly `SET LOCAL`, but accepts bind parameters
  (a literal `SET LOCAL x = $1` does not). Use it from Npgsql.
- The setting disappears at COMMIT/ROLLBACK, so a pooled connection cannot carry it to
  the next borrower. Add a pool check-in `DISCARD ALL` / `RESET ALL` as the second line.
- **Fail-closed:** unset, empty, or unknown-role context gives **0 rows**, not an error
  and not another tenant's rows (`nullif(current_setting(..., true), '')`, then NULL
  makes every comparison false). Test `04_*` checks this, including the reused-connection case.
- Garbage that is not a UUID / integer in the setting raises a cast error (still no data).
- The tenant/role values come from the verified JWT only, never from request input.

## Smoke tests

```bash
OWNER_URL=postgres://postgres:<pw>@localhost:5432/<db> \
APP_URL=postgres://app_user:<pw>@localhost:5432/<db> \
  bash db/tests/run.sh
```

`00_fixture.sql` runs as owner (idempotent seed: 2 tenants, 5 docs, 6 chunks, one-hot
embeddings with the other tenant sitting on the query point). `01`-`07` run as
`app_user`, each in a transaction that rolls back, asserting with `RAISE EXCEPTION`
(psql `ON_ERROR_STOP` makes the runner exit non-zero). `01` fails if the connection is
not a locked-down role, because otherwise every "0 rows" result is hollow.

They cover: cross-tenant select = 0 rows; wrong role = 0 rows; hr-admin inheritance;
app filter == RLS; fail-closed / SET LOCAL non-leak; WITH CHECK on writes; audit
UPDATE/DELETE/TRUNCATE denied; HNSW index present. They do **not** replace EOGHAN's
200-query leakage suite, the dropped-policy negative control, or the large-table
`returned == k` test (the fixture is too small to force the index-window problem).

## Things to know

- The owner must be a **superuser** (the official image's `POSTGRES_USER` is). `FORCE ROW
  LEVEL SECURITY` would block a plain non-superuser owner from seeding.
- The app role cannot move a document to another tenant (WITH CHECK pins `tenant_id` to
  the caller's). Moving a document across tenants is an owner/admin operation.
- `app_user` can only create documents/chunks at or below its own role level.
- `tenants`, `users`, `roles` are read-only for `app_user`; seeding is owner-side.
- `audit_log` is tenant-scoped by RLS; who may *call* `/audit` is an API rule.
