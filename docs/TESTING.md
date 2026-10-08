# Testing — MultiTenantRAGagnets

How the service is proved to work. The evaluation harness produces the real numbers
that are the whole point of this project — there is no CV bullet without measured
numbers. For the build order of the test steps see
[`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) (steps 8, 9, 12, 13); for the
security constraints being verified see [`TRD.md`](./TRD.md) §7.

The whole harness runs from **one documented command** from a clean clone (after
`docker compose up`), and the same command runs in CI on every push.

The leakage and RLS specifics below were proven in the RLS + pgvector spike against
real Postgres 16 / pgvector 0.8.7. They are not theoretical.

---

## 1. Leakage suite — the credibility anchor

**N = 200** adversarial cross-tenant and cross-role queries. Each query is designed
to make similarity search *want* to return a forbidden chunk — e.g. a tenant-B
document sitting on the query point, or a role-scoped document a lower role is
asking around. **Pass = 0 leaked chunks.** The suite runs in three modes:

### 1a. App-layer run

The full service path. Assert that no returned chunk, and no chunk sent to the LLM,
belongs to another tenant or a role above the caller's. This is what the service
actually does.

### 1b. RLS-only run (app filter removed)

Run the **same 200 queries as the `app_user` DB role with the application filter
stripped out**, so only Postgres RLS stands between the query and the data. Assert
**0 leaked chunks** again. This proves the database layer holds **independently** of
the app filter — the whole point of defence in depth ([`TRD.md`](./TRD.md) §2).

This run **must** cover the three traps the spike measured:

- **Cross-tenant nearest-neighbour** — a tenant-B row is the true nearest neighbour;
  tenant A must still get only its own rows.
- **Connection-reuse-without-reset** — reuse a pooled connection for a second
  tenant without re-setting the tenant; a fail-closed policy returns 0 rows, never
  the previous tenant's (this is what catches the `SET LOCAL` regression).
- **The under-return assertion** — see §2.

### 1c. Negative control (deliberately-dropped policy) — MUST fail

Run the RLS-only suite **with the RLS policy dropped**. This run **must leak** — if
it returns 0 leaks, the test proves nothing (the app filter is masking the DB
layer, or the queries don't actually bait a leak). EOGHAN confirms the negative
control **fails loudly** before anyone trusts a green result on 1a/1b.

> This is the single most important check in the project: a security test you have
> never seen fail is not evidence. The dropped-policy run is how we know the suite
> *can* catch a leak.

---

## 2. HNSW under-return assertion (`returned == k`)

A filtered approximate (HNSW) search under RLS can silently return **fewer than k**
rows — the spike measured **0 of 5** returned on the forced index path despite 60
matching rows, because the index window filled with rows the filter then stripped.
This is a correctness/availability bug, **not a leak**, but it ships green because
dev tables use a seq scan and only large tables hit the index path.

**Assert `returned_count == k`** (or `== available`, when fewer than k permitted
chunks exist) on a tenant whose neighbours are **dominated by another tenant**, with
the index path forced. This fails unless iterative index scans are enabled
([`TRD.md`](./TRD.md) §7.3). Without this assertion the bug ships green.

---

## 3. Retrieval quality — recall@k and MRR

Over the **labelled question set** (built with the synthetic corpus, step 6), with a
manifest of expected-visible chunk ids per `(tenant, role)`:

- **recall@k** — fraction of expected chunks retrieved in the top k.
- **MRR** — mean reciprocal rank of the first relevant chunk.

Wrong labels make these meaningless, so the labelled set and its manifest are part
of the corpus generator, not hand-waved. If HNSW approximation under RLS makes
recall look unstable, use an exact-search fallback (or tuned `ef_search`) **for the
labelled eval only**, and state which was used.

## 4. p95 latency

Report **p95** for retrieval and for end-to-end (retrieval + LLM), **with corpus
size (chunk count) and the reference hardware stated alongside every number**. A
latency number without corpus size and hardware is not reproducible and does not go
in the results file or the CV bullet.

## 5. Audit-row tests

- Every query writes **exactly one** audit row (who/tenant/role, when, query hash,
  chunk ids retrieved, chunk ids sent to LLM, model, latency).
- **Audit integrity:** the chunk ids recorded as *retrieved* and *sent to the LLM*
  match the response's cited chunk ids. This is why retrieved and sent are stored
  **separately** ([`APP_FLOW.md`](./APP_FLOW.md) §3) — a single combined field makes
  the test hollow.
- **Append-only:** an `UPDATE` or `DELETE` on `audit_log` as the app DB role is
  rejected (the role has no such grant), not merely avoided by the app.

## 6. Keyless CI with the fake LLM

CI runs the **entire harness, including all 200 leakage queries, with no secrets**:

- GitHub Actions spins up a Postgres + pgvector service.
- The **fake/deterministic provider** carries the run — `FakeChatProvider` plus the
  deterministic real-shaped embedding ([`TRD.md`](./TRD.md) §5). The security
  property does not depend on answer quality, so the fake provider proves it.
- **Ollama is not in CI** — its image is on an optional compose profile and is never
  pulled. Any contributor can run the leakage suite on a clean clone without an API
  key or a model download.
- The results file is published as a build artifact.

## 7. Access-change test

Revoke a role, or move a document to a different tenant/level, and assert the change
is reflected on the **next query with no re-embed** — because ACLs live on the
document and are joined at query time, not copied onto chunks
([`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) step 12). A stale ACL would be
a leak; this test is what proves it isn't.

---

## Definition of done (tests)

1. Leakage suite: 200 queries, **0 leaked chunks** at the app layer (1a) **and** the
   RLS-only layer (1b), with the dropped-policy negative control (1c) **failing**.
2. `returned == k` under a hostile neighbour distribution on the forced index path.
3. recall@k and MRR reported over the labelled set.
4. p95 (retrieval + end-to-end) reported with corpus size and hardware.
5. Every query audited; audit-integrity and append-only tests green.
6. Access change reflected on the next query with no re-embed.
7. The whole harness runs keyless in CI on the fake LLM, results published.
