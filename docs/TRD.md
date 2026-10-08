# Technical Requirements — MultiTenantRAGagnets

Permission-aware, multi-tenant Retrieval-Augmented Generation (RAG) service.
This document says **what the service is and how it works technically**. For the
request/data flows see [`APP_FLOW.md`](./APP_FLOW.md); for the build order see
[`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md); for how it is verified see
[`TESTING.md`](./TESTING.md).

## 1. Problem and scenario

The scenario is one a SaaS vendor recognises: an "ask your documents" assistant
sold to customer companies. Each customer company is a **tenant**. Inside a tenant,
staff see different things — an HR policy is everyone's, a salary-band memo is
HR-only, a board pack is execs-only.

The naive RAG demo puts every chunk in one vector index and trusts the prompt to
keep secrets. That leaks the moment similarity search returns another tenant's
chunk, or a chunk above the caller's role. Prompt instructions do not stop it,
because the forbidden data is already in the context window.

This service makes **"the model can only see what the caller may see" a property of
the data layer** — enforced before anything reaches the LLM, provable by tests, and
recorded in an audit trail a compliance officer (GDPR, DORA) would ask for.

**Actors:** the API caller (an authenticated user acting as a tenant + role); the
tenant admin who ingests documents and sets access; the auditor who reads the
retrieval log.

## 2. The core guarantee

> A retrieval returns only chunks the caller is permitted to see, and only those
> chunks are sent to the LLM. This is enforced at the **data layer**, not requested
> of the model.

The guarantee is enforced **twice**, independently:

1. **Application layer** — the retrieval query filters on `tenant_id` and the
   caller's role before the vector search result leaves the service.
2. **Database layer** — Postgres **row-level security (RLS)** policies re-apply the
   same tenant + role constraint, so even a query with the app filter removed
   returns nothing the caller may not see.

RLS is **defence in depth** — a second, independent enforcement layer. It is not a
substitute for the app filter, and the app filter is not a substitute for it. The
leakage suite proves both layers hold independently (see [`TESTING.md`](./TESTING.md)).

## 3. Stack

| Concern | Choice |
|---------|--------|
| Service | ASP.NET Core (.NET) Web API |
| Store | Postgres 16 with the **pgvector** extension (vectors + RLS in one engine) |
| Isolation | Postgres **row-level security**, keyed on per-request session settings |
| Embeddings + chat | A provider abstraction (see §5) — local **Ollama** by default, a **fake** provider for CI |
| Identity | Locally issued JWT carrying `tenant` + `role` (no SSO) |
| Surface | Swagger UI; optionally a minimal static page |
| Run | `docker compose up` brings up the API and Postgres |

Embedding dimension is **fixed per index** and sticky once an index exists (e.g.
`nomic-embed-text` → 768, `mxbai-embed-large` → 1024). The chosen Ollama embedding
model therefore has to be decided before the first index is built.

## 4. Access model

- **Tenants:** 2 in the MVP. A tenant is a hard wall — role power never breaches it.
- **Roles:** `employee`, `manager`, `hr-admin`, modelled as an **integer level**
  (`employee=1 < manager=2 < hr-admin=3`). A document is visible when
  `required_level <= caller_level`, so **`hr-admin` inherits `manager` and
  `employee`** (hierarchy, not a flat ACL). A higher role sees everything a lower
  role sees within the same tenant, and nothing across the tenant wall.
- **Access metadata lives on the document, joined at query time** — never copied
  onto chunks. Revoking a role or moving a document is reflected on the **next
  query with no re-embed**. Stale ACLs copied onto chunks would be a leak; this
  design removes that class of bug.
- The tenant+role expansion (including the hierarchy) **must be expressed
  identically** in the app filter and the RLS policy, or the two layers disagree.

## 5. Provider abstraction

Embeddings and chat sit behind interfaces (`IEmbeddingProvider`, `IChatProvider`)
so the model is swappable and the security tests run without a key:

- **Ollama** — the default local provider. Free, nothing leaves the machine, no API
  key. Used for real answers and real embeddings in dev.
- **Fake / deterministic** — `FakeChatProvider` plus a **deterministic embedding**
  (hash → vector of the fixed dimension). Keyless, deterministic, and the path CI
  runs on. The fake embedding is *real-shaped* (same dimension as the real model)
  so vector search is genuinely exercised without pulling a model.

The leakage suite proves the **security property**, which does not depend on answer
quality, so it runs entirely on the fake provider in CI with no secrets. Ollama is
kept off the default `docker compose` profile so CI never pulls the heavy image.

## 6. Audit log

Every query writes exactly **one audit row**:

- who (user, tenant, role), when (timestamp);
- the query text or a hash of it;
- **chunk ids retrieved** and **chunk ids sent to the LLM**, recorded *separately*;
- the model used; the latency.

The log is **append-only, enforced by the database role** — the app's DB role has
no `UPDATE` or `DELETE` grant on the audit table. Recording retrieved-vs-sent
separately is what makes the audit integrity test meaningful (see
[`TESTING.md`](./TESTING.md)).

## 7. Security design constraints (load-bearing, non-negotiable)

These come from the RLS + pgvector spike run against real Postgres 16 / pgvector
0.8.7. Each was measured, not assumed. They are **requirements**, not suggestions —
violating any one reintroduces a leak or a silent correctness bug.

1. **Connect as a locked-down role.** The app's request path **must** connect as a
   role that is **not the table owner, not a superuser, and not `BYPASSRLS`**
   (`NOSUPERUSER NOBYPASSRLS`, non-owner). A superuser or `BYPASSRLS` role ignores
   RLS entirely — the spike measured the owner seeing *all* tenants' rows even with
   `FORCE ROW LEVEL SECURITY` on. Migrations run as the owner; requests never do.
   Set `FORCE ROW LEVEL SECURITY` as belt-and-braces, but never rely on it against a
   superuser.

2. **Set the tenant with `SET LOCAL` inside the query transaction.** Pooled
   connections are reused; a plain session `SET` survives into the next borrower of
   the connection and leaks across tenants. The spike reproduced exactly this — a
   tenant-B request saw tenant-A rows off a stale setting. `SET LOCAL` cannot
   outlive its transaction, so it cannot leak. Add a pool check-in reset hook
   (`DISCARD ALL` / `RESET ALL`) as a second line. Because policies read
   `current_setting(..., true)`, an unset session is **fail-closed** (returns 0
   rows, not another tenant's).

3. **Iterative index scans for filtered ANN.** A filtered HNSW (approximate)
   search under RLS can silently return **fewer than k** rows — the index window
   fills with rows the filter then strips, and `LIMIT k` under-fills. The spike saw
   0 of 5 returned on the forced index path despite 60 matching rows. This is a
   correctness/availability bug, **not a leak** (isolation still held), but it ships
   green because dev tables use a seq scan and only large prod tables hit the index
   path. Fix: enable pgvector **iterative index scans**
   (`hnsw.iterative_scan = strict_order` or `relaxed_order`, pgvector ≥ 0.8) on the
   request path; prefer `strict_order` when exact k-nearest ordering matters.
   Bumping `ef_search` is a crutch, not the fix. Keep `tenant_id`/role in the `WHERE`
   too so the planner has selectivity.

4. **One combined tenant+role policy.** Express tenant **and** role as a **single
   combined policy** (`tenant AND role`). Two separate **permissive** policies are
   combined by Postgres with **OR**, which *widens* access — the spike measured a
   manager seeing a level-3 memo *and* another tenant's document. If the rules are
   split for readability, the narrowing rule **must** be `RESTRICTIVE` (AND).
   Give every policy an explicit `WITH CHECK` so ingest cannot mislabel a chunk's
   tenant. This was the single biggest RLS footgun found.

## 8. Non-goals (v1)

Explicitly out of scope for the first shippable version:

- **No UI beyond Swagger** (a minimal static page at most).
- **No SSO / real identity provider** (OIDC, Entra ID). Locally issued JWTs only.
- **No GraphRAG**, knowledge graphs, or agentic multi-step retrieval.
- **No semantic caching** — it is a leakage risk in its own right and is a v2 story.
- No rerankers / hybrid-search tuning / fine-tuning; no more than 2 tenants / 3
  roles; no real customer data; no production hosting.

### Named but not fully defended (state in the threat model)

- **Prompt injection inside documents.** Filtering *before* the LLM means an
  injected "ignore previous instructions" in a tenant's own document cannot pull
  another tenant's data — it was never in context — but it can still distort the
  answer. v1 includes a few injection docs in the corpus and names the limit in the
  threat model; full mitigation is out of scope.
- **Token forgery.** The JWT is the identity source; v1 does not defend a forged
  token beyond standard signature verification. The signing key lives in
  env / user-secrets, **never committed**.
