<div align="center">

# 🛡️ MultiTenantRAGagnets

### Permission-aware, multi-tenant RAG where the **database** keeps the secrets, not the prompt.

![Status](https://img.shields.io/badge/status-design%20stage-orange?style=for-the-badge)
![.NET](https://img.shields.io/badge/ASP.NET%20Core-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Postgres](https://img.shields.io/badge/Postgres%2016-4169E1?style=for-the-badge&logo=postgresql&logoColor=white)
![pgvector](https://img.shields.io/badge/pgvector-HNSW-336791?style=for-the-badge)
![Ollama](https://img.shields.io/badge/Ollama-local%20LLM-000000?style=for-the-badge)
![Docker](https://img.shields.io/badge/docker%20compose-2496ED?style=for-the-badge&logo=docker&logoColor=white)

**[The idea](#-the-idea)** · **[How it works](#-how-it-works)** · **[Stack](#-stack)** · **[Proof, not promises](#-proof-not-promises)** · **[Docs](#-docs)**

</div>

> [!NOTE]
> **Status: design stage.** This repo holds the planning and context docs only. There is no code yet, and no measured results yet. Every number promised below is something the build must produce, not something already achieved.

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

The build is designed around an eval harness that is built **early, not last**. The plan is to measure and publish:

- 🚫 **Leakage suite.** 200 adversarial queries across tenants and roles, expecting zero leaked chunks, with a **negative control** (filters off) to prove the test can actually fail, and an **RLS-only run** to prove the database lock stands alone.
- 🎯 **Retrieval quality.** recall@k and MRR over a labelled question set.
- ⏱️ **p95 latency.** Retrieval and end-to-end, with corpus size and hardware stated.
- 🔑 **Keyless CI.** The entire harness runs on GitHub Actions with the fake LLM and no secrets.

## 🗺️ Roadmap

A 15-step build, roughly three weeks of evenings, with a defined MVP cut line. See [`docs/IMPLEMENTATION_PLAN.md`](./docs/IMPLEMENTATION_PLAN.md).

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
