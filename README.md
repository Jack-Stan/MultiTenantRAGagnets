<div align="center">

# 🛡️ MultiTenantRAGagnets

### Permission-aware, multi-tenant RAG where the **database** keeps the secrets, not the prompt.

[![CI](https://img.shields.io/github/actions/workflow/status/Jack-Stan/MultiTenantRAGagnets/ci.yml?branch=main&style=for-the-badge&label=CI)](https://github.com/Jack-Stan/MultiTenantRAGagnets/actions/workflows/ci.yml)
![Status](https://img.shields.io/badge/status-MVP%20built-brightgreen?style=for-the-badge)
![.NET](https://img.shields.io/badge/ASP.NET%20Core-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Postgres](https://img.shields.io/badge/Postgres%2016-4169E1?style=for-the-badge&logo=postgresql&logoColor=white)
![pgvector](https://img.shields.io/badge/pgvector-HNSW-336791?style=for-the-badge)
![Ollama](https://img.shields.io/badge/Ollama-local%20LLM-000000?style=for-the-badge)
![Docker](https://img.shields.io/badge/docker%20compose-2496ED?style=for-the-badge&logo=docker&logoColor=white)

**[The idea](#-the-idea)** · **[How it works](#-how-it-works)** · **[Stack](#-stack)** · **[Proof, not promises](#-proof-not-promises)** · **[Docs](#-docs)**

</div>

> [!NOTE]
> **Status: MVP built, with measured results.** The service, schema, RLS policies and the leakage harness are in this repo, and CI runs the whole harness against a real `pgvector/pgvector:pg16` container on every push. Numbers below come from [`results/`](./results/) and are stated with their caveats. Retrieval quality has been measured once with real Ollama embeddings on a synthetic corpus (see below, with its limits). Not yet done: a hosted deployment, and any quality claim on real documents.

---

## 💡 The idea

The naive RAG demo drops every chunk into one vector index and asks the prompt nicely to keep secrets. That leaks the moment similarity search returns **another tenant's chunk**, or a chunk **above the caller's role**. Prompt instructions can't fix it, because the forbidden data is already in the context window.

This design makes one guarantee, enforced at the data layer:

> **A retrieval returns only chunks the caller is permitted to see, and only those chunks are sent to the LLM.**

| 🏢 Tenants | 👥 Roles | 📜 Audit |
|---|---|---|
| Each customer company is isolated from every other | An HR policy is everyone's, a salary memo is HR-only, a board pack is execs-only | Every retrieval is recorded in an append-only log a compliance officer (GDPR, DORA) would ask for |

## 🔐 Two locks, enforced independently

```mermaid
flowchart LR
    Q([Question + JWT<br/>tenant + role]) --> L1
    subgraph L1 [🔒 Lock 1 — application layer]
        direction TB
        F1[WHERE tenant_id AND required_level]
    end
    L1 --> L2
    subgraph L2 [🔒 Lock 2 — database layer]
        direction TB
        F2[Postgres row-level security<br/>re-applies tenant AND role]
    end
    L2 --> C[Permitted chunks only]
    C --> LLM[LLM]
    C --> A[(Audit log)]
    LLM --> R([Answer + cited chunk ids])
```

Row-level security is **defence in depth**, not a replacement for the app filter, and the app filter is not a replacement for it. The test suite proves each lock holds on its own.

## ⚙️ How it works

<details>
<summary><b>📥 Ingestion</b> — upload, chunk, embed, store</summary>

<br/>

```mermaid
flowchart TD
    A[Upload: document + tenant_id + allowed roles] --> B[Chunk]
    B --> C[Embed via provider<br/>Ollama in dev, fake in CI]
    C --> D[(Postgres + pgvector<br/>HNSW index)]
    A --> F[Document row: tenant + access level]
    F --> D
```

Access metadata lives on the **document** and is joined at query time, never copied onto chunks. A revoke or a document move needs **no re-embed**.

</details>

<details>
<summary><b>🔎 Query</b> — filter first, then search, then answer</summary>

<br/>

1. Verify the JWT and extract `tenant` + `role`.
2. `BEGIN`, then `SET LOCAL app.tenant_id` and `app.role_level`. `SET LOCAL` (never a session `SET`) so a pooled connection can't leak context to the next request.
3. Vector search with the tenant and role predicate, with RLS re-applying it underneath.
4. Iterative index scans keep probing until `k` post-filter rows come back, so the filtered search never silently under-returns.
5. Only permitted chunks go into the prompt. The answer cites the chunk ids it used.

</details>

<details>
<summary><b>🧾 Audit</b> — every query leaves a trail</summary>

<br/>

Each query writes an append-only audit row. Full flow in [`docs/APP_FLOW.md`](./docs/APP_FLOW.md).

</details>

## 🧰 Stack

| Concern | Choice |
|:--|:--|
| **Service** | ASP.NET Core (.NET) Web API |
| **Store** | Postgres 16 + **pgvector** (vectors and RLS in one engine) |
| **Isolation** | Postgres **row-level security** keyed on per-request session settings |
| **Embeddings + chat** | Provider abstraction: local **Ollama** by default, a deterministic **fake** for CI |
| **Identity** | Locally issued JWT carrying `tenant` + `role` (no SSO) |
| **Surface** | Swagger UI |
| **Run** | `docker compose up` |

## 🧪 Proof, not promises

The harness was built early, not last, and runs keyless in CI (fake LLM, no secrets). Latest committed run: [`results/leakage-results.md`](./results/leakage-results.md), Postgres 16.15 + pgvector 0.8, 4-core GitHub runner.

| Check | Result |
|:--|:--|
| 🚫 **Leakage, full service** (app filter + RLS) | **0 leaks** over 200 adversarial queries, natural and aimed straight at forbidden chunks |
| 🚫 **Leakage, RLS only** (app filter removed) | **0 leaks**, same 200 queries, both variants |
| 🧪 **Negative control, RLS disabled** | **1,868 leaks** detected (735 cross-tenant, 126 cross-role), so the harness can fail |
| 🧪 **Negative control, tenant-only policy** | **880 leaks**, all cross-role, none cross-tenant: the role clause does real work |
| 🎯 **`returned == k`** with 2,500 hostile neighbour rows | exactly `min(k, available)` at k = 1, 5, 10, 20; with iterative scan off it returns 0 |
| ⏱️ **p95 retrieval / end-to-end** (planner default) | 1.4 ms / 2.0 ms on 2,671 rows, fake LLM (about 0 ms), shared CI runner. Forced HNSW path: 6.2 ms / 6.9 ms |
| 🎯 **Retrieval quality, real embeddings** (`nomic-embed-text`, hard synthetic corpus, 1,910 chunks, 192 distinct questions) | exact search: **recall@5 0.812, MRR@5 0.625**, recall@10 0.905. Forced HNSW: recall@5 0.789, MRR@5 0.608. Weakest: multi-chunk answers (only 35% complete in the top 5) and superseded editions (near-duplicate MRR@5 0.50). 0 forbidden chunks returned across 384 above-role probes per variant |

### Retrieval quality, in detail

Measured by the manual `quality-ollama` workflow on the `hard` corpus profile, CI run [38719470320](https://github.com/Jack-Stan/MultiTenantRAGagnets/actions/runs/38719470320) on `main`, results in [`results/quality-results-ollama-hard.md`](./results/quality-results-ollama-hard.md). Real Ollama embeddings for ingest and query, fake chat model, Postgres 16 + pgvector 0.8 on a 4-core shared runner.

| Category (exact search, k = 5) | Distinct questions | recall@5 | MRR@5 | complete@5 |
|:--|--:|--:|--:|--:|
| keyword | 12 | 1.000 | 0.898 | 1.000 |
| distractor | 12 | 1.000 | 0.721 | 1.000 |
| near-duplicate (superseded editions) | 12 | 0.977 | 0.501 | 0.977 |
| reference | 48 | 0.893 | 0.672 | 0.906 |
| paraphrase | 72 | 0.760 | 0.492 | 0.819 |
| multi-chunk | 36 | 0.626 | 0.733 | 0.347 |

What this says: keyword and distractor questions are solved, but the embedding model does not separate a current policy from its superseded edition (the top hit is a stale edition about half the time), and answers spread across two chunks are often incomplete at k = 5. Both are retrieval-quality gaps, not leakage gaps. The standard (template) corpus scores 1.000 on every metric, which is a ceiling and not a result, so it is not quoted here.

**Read these honestly:**

- The corpus is hand-templated synthetic text with a fixed seed. It is harder than the standard corpus by construction but it is not real documents, so read the numbers as "this model on this corpus", not as a general claim or a comparison with public benchmarks.
- Category buckets are small (12 to 72 distinct questions) and each figure is a single run per CI job with no confidence intervals. Treat differences of a few points as noise. Two earlier runs agreed to within 0.004 overall on the HNSW paths and exactly on exact search.
- Forced HNSW sits 2 to 5 points under exact search with `returned == min(k, visible)` holding, which looks like approximation loss rather than under-return. The planner default matches HNSW at k = 5 and exact search at k = 10; this has not been investigated (likely the planner choosing differently by `LIMIT`, unverified).
- The exact-scan fallback (added after a real under-return was found: HNSW graph builds can leave rows unreachable) fired 0 times in this run. It masks that problem rather than repairing the graph.
- Embeddings are the raw text with no nomic task prefixes, which is how the provider ships.
- Embeddings in the keyless CI harness are a deterministic hash with no semantics, so the **recall@5 = 0.345 / MRR = 0.196** the leakage suite also writes are harness smoke values, not a quality result. To reproduce the quality run, trigger the `quality-ollama` workflow (Actions tab, "Run workflow", `corpus: hard`), or locally set `EMBEDDINGS=ollama`, `OLLAMA_URL` and `CORPUS=hard` and run the `OllamaQualityTests` test.
- Latency is for about 2.7k rows on a shared runner with a fake LLM. It is not a production claim and says nothing about real LLM latency.
- Zero leaks proves the filter and database layers hold under these 200 queries. It does not cover prompt-injection distortion, which is out of scope below.

## 🗺️ Roadmap

A 15-step build with a defined MVP cut line, in [`docs/IMPLEMENTATION_PLAN.md`](./docs/IMPLEMENTATION_PLAN.md). Steps 1 to 13 are done and proven in CI, including the access-change test (a revoke shows on the next query with no re-embed) and the audit-integrity test. The real-embeddings quality run is published above. Next: a ranking fix for superseded editions (a version or recency preference is the obvious candidate), better multi-chunk handling, and per-tenant or partial HNSW indexes so the exact-scan fallback is a safety net instead of a mask.

## 🚧 Honest threat model

**Defends against:** cross-tenant and cross-role retrieval leakage, via the app filter plus RLS.

**Does not fully defend against:** prompt-injection distortion, token forgery beyond standard signature verification, or the v2 semantic-cache leak.

## 📚 Docs

Read these before writing any code. Start with the TRD.

| | File | What it answers |
|:-:|:--|:--|
| 📐 | [`docs/TRD.md`](./docs/TRD.md) | What the service is, the stack, the access model, security constraints, non-goals |
| 🔀 | [`docs/APP_FLOW.md`](./docs/APP_FLOW.md) | Ingestion, query and per-query audit flows |
| 🛠️ | [`docs/IMPLEMENTATION_PLAN.md`](./docs/IMPLEMENTATION_PLAN.md) | The phased 15-step build order and the MVP cut line |
| ✅ | [`docs/TESTING.md`](./docs/TESTING.md) | Leakage suite, negative control, retrieval quality, latency, keyless CI |
