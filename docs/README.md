# Docs

The build-context set for **MultiTenantRAGagnets**: the single source of truth a builder (human or AI) reads **before writing any code**, so context is organised up front instead of guessed at mid-build.

The project overview lives in the [root README](../README.md).

| File | What it answers |
|------|-----------------|
| [`TRD.md`](./TRD.md) | What the service is and how it works technically; the stack, the access model, the non-negotiable security constraints, the non-goals. |
| [`APP_FLOW.md`](./APP_FLOW.md) | The request/data flows: ingestion, query, and the per-query audit write. |
| [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) | The phased 15-step build order, the eval-harness-early sequencing, and the MVP cut line. |
| [`TESTING.md`](./TESTING.md) | How you prove it works: the leakage suite, the negative control, retrieval quality, latency, audit tests, keyless CI. |

Start with `TRD.md`.
