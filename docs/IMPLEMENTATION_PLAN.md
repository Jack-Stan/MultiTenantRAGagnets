# Implementation Plan — MultiTenantRAGagnets

The build order for the MVP, in phases. Each step says what it builds and what it
depends on. For what the service *is* see [`TRD.md`](./TRD.md); for the flows see
[`APP_FLOW.md`](./APP_FLOW.md); for how each test step is run and what it must prove
see [`TESTING.md`](./TESTING.md).

## Sequencing principle: the eval harness is built early, not last

> The **evaluation harness is built in Phase 3 (Week 2), against a thin vertical
> slice — not at the end.** The measured numbers (leakage, recall@k/MRR, p95) are
> the whole point. They are deliberately the thing that lands *first*, before the
> full API, audit, and polish. Nothing that produces a number is allowed to slip
> into the final evening.

Owners: **RÓISÍN** data · **PÁDRAIG** backend · **AOIFE** frontend (Swagger only) ·
**EOGHAN** test. The data-layer steps (2, 3, 5) are fed by the RLS spike findings —
connect-as-non-owner, `SET LOCAL` pooling reset, HNSW-under-RLS row counts, one
combined policy — which are the security constraints in [`TRD.md`](./TRD.md) §7.

---

### Phase 0 — Skeleton & infra

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 1 | Repo + solution skeleton + `docker compose` | PÁDRAIG | `.sln`; `src/TenantSafeRag.Api`, `src/TenantSafeRag.Core`, `tests/`; `docker-compose.yml` (pin `pgvector/pgvector:pg16` + `ollama/ollama`, **Ollama on an optional compose profile so CI never pulls it**); `.editorconfig`, `.gitignore`, `LICENSE` (MIT), README stub | — |

### Phase 1 — Data layer (uses the spike)

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 2 | Schema + migrations; chunk + **doc-level** access metadata | RÓISÍN | migrations for `tenants`, `users`, `documents` (`tenant_id`, `required_level`/`allowed_roles`), `chunks` (`doc_id`, `tenant_id`, `embedding`, `text`, `version`), `audit_log` (append-only); HNSW index. **ACLs live on the document, joined at query time — never copied onto chunks**, so a revoke needs no re-embed. Embedding dimension fixed once by the chosen Ollama embed model. | 1 |
| 3 | RLS policies + non-owner app role | RÓISÍN | `app_user` role (**non-owner, `NOSUPERUSER`, `NOBYPASSRLS`**); **one combined** RLS policy keyed on `current_setting('app.tenant_id', true)` and `app.role_level`, with explicit `WITH CHECK`; `SET LOCAL`-per-request + pool-reset pattern. See [`TRD.md`](./TRD.md) §7.1, §7.2, §7.4. | 2 |

### Phase 2 — Thin retrieval slice + fake LLM (gives the harness a target early)

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 4 | Provider interfaces + fake LLM + Ollama impl | PÁDRAIG | `IEmbeddingProvider`, `IChatProvider`; `FakeChatProvider` (deterministic, keyless — the CI path); `OllamaProvider`; **deterministic real-shaped embedding for CI** (hash → vector of the fixed dim) so vector search is exercised without a model pull | 1 |
| 5 | Retrieval service — tenant+role filter BEFORE the LLM, at **app layer AND RLS** | PÁDRAIG + RÓISÍN | the query flow: `SET LOCAL` tenant/role → SQL filter on `tenant_id` + role → **iterative-scan** vector search → permitted chunks only → prompt. The `hr-admin` inherits `employee` hierarchy must be expanded **identically** in the app filter and the RLS policy. See [`APP_FLOW.md`](./APP_FLOW.md) §2. | 3, 4 |

### Phase 3 — Evaluation harness (built now, against the thin slice)

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 6 | Synthetic corpus generator + labelled question set | RÓISÍN | generator: near-identical docs across the 2 tenants with distinct secrets, role-scoped docs, a few prompt-injection docs; labelled Q&A set for recall@k/MRR; a manifest of expected-visible chunk ids per `(tenant, role)` | 2 |
| 7 | Ingestion pipeline: upload → chunk → embed → store | PÁDRAIG + RÓISÍN | the ingest path (see [`APP_FLOW.md`](./APP_FLOW.md) §1); chunking params fixed and recorded | 4, 6 |
| 8 | **Leakage suite (N=200)** — the credibility anchor | EOGHAN | 200 adversarial cross-tenant/cross-role queries; **app-layer run** + **RLS-only run** (app filter removed, as `app_user`); a **deliberately-dropped-policy run that MUST fail**; asserts **0 leaked chunks**; writes a committed results file. Detail in [`TESTING.md`](./TESTING.md) §1. | 5, 7 |
| 9 | recall@k / MRR + p95 latency | EOGHAN | metrics over the labelled set; p95 for retrieval and end-to-end, **corpus size + hardware stated**; one command runs the whole harness → report. Detail in [`TESTING.md`](./TESTING.md) §3, §4. | 6, 7, 8 |

### Phase 4 — Audit, API, access change

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 10 | Audit log writer (append-only) | PÁDRAIG | one row per query: who/tenant/role, when, query hash, **chunk ids retrieved vs sent to the LLM** (separately), model, latency; append-only enforced by the DB role (no `UPDATE`/`DELETE` grant). See [`APP_FLOW.md`](./APP_FLOW.md) §3. | 5 |
| 11 | API surface + locally-issued JWT + Swagger | PÁDRAIG + AOIFE (Swagger only) | `/ingest`, `/query`, `/audit`; JWT carrying `tenant`+`role`; Swagger UI; optional minimal static page. **Signing key in env / user-secrets, never committed.** | 5, 10 |
| 12 | Access-change path + test | RÓISÍN + EOGHAN | revoke-role / move-doc reflected on the **next query with no re-embed** (ACL joined at query time); test proving it | 2, 5 |

### Phase 5 — Audit test, CI, README

| # | Step | Owner | Builds | Depends |
|---|------|-------|--------|---------|
| 13 | Audit integrity test | EOGHAN | test that retrieved vs sent-to-LLM chunk ids match the response citations. See [`TESTING.md`](./TESTING.md) §5. | 8, 10, 11 |
| 14 | CI pipeline — keyless | PÁDRAIG + EOGHAN | GitHub Actions: spin Postgres+pgvector service, run unit + integration + **full leakage suite with the fake LLM, no secrets**; publish the results file as an artifact. See [`TESTING.md`](./TESTING.md) §6. | 8, 9, 13 |
| 15 | README + threat model + measured CV bullet | (planner) + PÁDRAIG | README: scenario, architecture diagram, **threat model** (defends cross-tenant/cross-role retrieval leakage via app+RLS; does **not** fully defend prompt-injection distortion, token forgery, or the v2 semantic-cache leak), results table, CV bullet with the real numbers | 9, 13, 14 |

**Verified in the real app, not just in theory:** from a clean clone,
`docker compose up` + one documented command runs the service and the full harness;
step 14 CI runs the same harness on every push. EOGHAN owns steps 8, 9, 12, 13 — the
plan is not done without them.

---

## MVP cut line

**In the first shippable version (all of steps 1–15):** 2 tenants / 3 roles
(`employee`/`manager`/`hr-admin`, hr-admin inherits employee), synthetic corpus,
ingest/query/audit, app+RLS double enforcement, the 200-query leakage suite **with
its negative control and RLS-only run**, recall@k/MRR, p95 latency,
`docker compose up`, Swagger, keyless CI with the fake LLM, README with threat model
+ measured CV bullet.

**Later (v2):** hosted-provider option, semantic caching (plus its own leakage
story), rerankers / hybrid-search tuning, more than 2 tenants / 3 roles, SSO/OIDC,
any UI beyond the minimal page, full prompt-injection mitigation, production hosting.

### If evenings run short — the cut-to-still-ship order

- **Non-negotiable** (these *are* the CV bullet): the leakage suite + negative
  control + RLS-only run (step 8); recall@k/MRR + p95 (step 9).
- **Trimmable to a stub:** the minimal static page (Swagger alone is fine); the
  access-change *suite* → a single scripted case; real Ollama answers → the fake LLM
  proves the security property, answer quality is secondary.

## Sequencing — ~3 weeks of evenings

- **Week 1 — steps 1–5.** Skeleton, docker-compose, schema, RLS + app role, provider
  interfaces + fake LLM, thin retrieval slice. *End state: a query returns
  tenant/role-filtered chunks through the fake LLM.*
- **Week 2 — steps 6–10.** Corpus generator + labelled set, ingestion, leakage suite
  + negative control, recall/MRR/p95, audit writer. *End state: the harness produces
  real numbers — the CV bullet is alive by the end of Week 2, by design.*
- **Week 3 — steps 11–15.** API + JWT + Swagger, access-change path + test,
  audit-integrity test, CI, README + threat model + CV bullet. Week 3 also absorbs
  the RLS-under-pooling gotchas.

## Top 3 risks to watch

1. **Pooled connections leaking the per-request tenant setting** (`SET LOCAL` not
   reset between checkouts) — a silent cross-tenant leak that app-layer tests pass
   straight through. *Guard:* the `SET LOCAL`-in-txn + pool-reset pattern
   ([`TRD.md`](./TRD.md) §7.2); the RLS-only harness run is the detector.
2. **HNSW approximate search under RLS** returns `< k` rows or changes the plan —
   recall looks bad/unstable and undercuts the headline. *Guard:* iterative index
   scans on ([`TRD.md`](./TRD.md) §7.3); the harness asserts `returned == k`; state
   corpus size with every number.
3. **A negative control that proves nothing** — if the dropped-policy run still
   returns 0 leaks because the app filter masks it, the credibility claim is hollow.
   *Guard:* the RLS-only run strips the app filter so a dropped policy genuinely
   leaks; EOGHAN confirms the negative control **fails loudly** before anyone trusts
   the green result ([`TESTING.md`](./TESTING.md) §1).
