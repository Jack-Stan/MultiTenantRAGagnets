# tenant-safe-rag — context docs (staging)

**This folder is a staging area, not the home of these docs.**

The four documents here (`TRD.md`, `APP_FLOW.md`, `IMPLEMENTATION_PLAN.md`,
`TESTING.md`) are the build-context set for **`tenant-safe-rag`** — a separate,
public GitHub repo that does **not exist yet** and could not be created from the
session that wrote these. They were assembled here so they are version-controlled
and reviewable in the meantime.

When `tenant-safe-rag` exists, copy these four files into its `docs/` directory
(i.e. `tenant-safe-rag/docs/TRD.md`, etc.) and delete this staging folder. They are
written as standard GitHub Markdown — plain repo docs, cross-referencing each other
by filename — precisely so the copy is a straight move with no rewriting.

## What these are for

They are the single source of truth a builder (human or AI) reads **before writing
any code**, so context is organised up front instead of guessed at mid-build.

| File | What it answers |
|------|-----------------|
| [`TRD.md`](./TRD.md) | What the service is and how it works technically; the stack, the access model, the non-negotiable security constraints, the non-goals. |
| [`APP_FLOW.md`](./APP_FLOW.md) | The request/data flows: ingestion, query, and the per-query audit write. |
| [`IMPLEMENTATION_PLAN.md`](./IMPLEMENTATION_PLAN.md) | The phased 15-step build order, the eval-harness-early sequencing, and the MVP cut line. |
| [`TESTING.md`](./TESTING.md) | How you prove it works: the leakage suite, the negative control, retrieval quality, latency, audit tests, keyless CI. |

Start with `TRD.md`.
