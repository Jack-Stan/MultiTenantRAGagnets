# MultiTenantRAGagnets

A permission-aware, multi-tenant Retrieval-Augmented Generation (RAG) service, designed so that **"the model can only see what the caller may see" is a property of the data layer**, not something requested of the prompt.

> **Status: design stage.** This repo currently holds the planning and context docs only. There is no code yet.

## The idea

The naive RAG demo puts every chunk into one vector index and trusts the prompt to keep secrets. That leaks the moment similarity search returns another tenant's chunk, or a chunk above the caller's role. Prompt instructions can't fix it, because the forbidden data is already in the context window.

This design enforces access **twice, independently**, before anything reaches the LLM:

1. **Application layer.** Retrieval filters on `tenant_id` and the caller's role.
2. **Database layer.** Postgres row-level security (RLS) re-applies the same constraint, so even a query with the app filter removed returns nothing the caller may not see.

Every retrieval is written to an append-only audit log.

## Planned stack

| Concern | Choice |
|---------|--------|
| Service | ASP.NET Core (.NET) Web API |
| Store | Postgres 16 + pgvector (vectors and RLS in one engine) |
| Isolation | Postgres row-level security |
| Embeddings and chat | Provider abstraction: local Ollama by default, a fake provider for CI |
| Identity | Locally issued JWT carrying `tenant` + `role` |
| Surface | Swagger UI |
| Run | `docker compose up` |

## Docs

Read these before writing any code. Start with the TRD.

| File | What it answers |
|------|-----------------|
| [`docs/TRD.md`](./docs/TRD.md) | What the service is, the stack, the access model, security constraints, non-goals |
| [`docs/APP_FLOW.md`](./docs/APP_FLOW.md) | Ingestion, query and per-query audit flows |
| [`docs/IMPLEMENTATION_PLAN.md`](./docs/IMPLEMENTATION_PLAN.md) | The phased 15-step build order and the MVP cut line |
| [`docs/TESTING.md`](./docs/TESTING.md) | The leakage suite, negative control, retrieval quality, latency, keyless CI |
