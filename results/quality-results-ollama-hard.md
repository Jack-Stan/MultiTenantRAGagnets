# Retrieval quality with real embeddings (Ollama)

Generated 2026-10-15 10:25:51Z, git `8716e87679c2cd69cd3a32512308d614e98c21ac`. Machine-readable copy: `quality-results-ollama-hard.json`.

**RETRIEVAL QUALITY with REAL embeddings (Ollama) for ingest and query, on the HARD synthetic corpus (near-duplicates across and within tenants, distractors, paraphrases, multi-chunk answers, above-role questions). Chat is the fake LLM. Synthetic and generated: read these numbers as 'this model on this corpus', not as a general claim.**

## Setup (stated with every number)

- Embedding model: `nomic-embed-text`, 768 dimensions, via Ollama 0.40.4. Ollama at localhost, CPU inference unless the host has a GPU; hardware below is the test runner's.
- Chat: Fake (deterministic; chat is not part of retrieval quality).
- Corpus: 2 tenants, 512 documents, 1910 base chunks (1828 distinct texts embedded in 270.7 s), seed 42; 4503 rows in `chunks` at measurement.
- Hostile rows: 2593 extra rows in 3 hostile tenants (fake hash vectors, never the target of a labelled question) are also in the chunks table, as in the leakage suite; they are invisible to the labelled callers under RLS.
- Labelled questions: 768 (192 distinct question texts).
- Chunking: fixed-window-chars, whitespace-snap, no trimming; size=500 chars; overlap=100 chars; wordSnapWindow=80 chars.
- Runner / hardware: GitHub Actions runner 'GitHub Actions 1000000815' (RUNNER_OS=Linux, ImageOS=ubuntu24); AMD EPYC 7763 64-Core Processor, 4 logical cores, 15.6 GiB RAM, Ubuntu 24.04.5 LTS, .NET 9.0.21.
- Database: PostgreSQL 16.15 (Debian 16.15-1.pgdg12+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14+deb12u1) 12.2.0, 64-bit, pgvector 0.8.7; on the same machine as the test runner (CI service container or local).

## recall@k and MRR@k

| Search path | k | Questions | recall@k | MRR@k |
|---|---|---|---|---|
| exact search (index scans off) | 5 | 768 | 0.812 | 0.625 |
| exact search (index scans off) | 10 | 768 | 0.905 | 0.635 |
| HNSW forced, iterative scan on | 5 | 768 | 0.789 | 0.608 |
| HNSW forced, iterative scan on | 10 | 768 | 0.879 | 0.617 |
| planner default, iterative scan on | 5 | 768 | 0.789 | 0.608 |
| planner default, iterative scan on | 10 | 768 | 0.905 | 0.635 |

## Hard corpus

- Documents: 512 = 96 current, 96 superseded editions, 120 distractor (guidance + FAQ), 192 noise, 8 prompt-injection.
- Question instances: 1152 = 768 answerable by their caller + 384 above the caller's role (none permitted). 192 distinct question texts.

| Category | Distinct texts | Answerable instances |
|---|---|---|
| keyword | 12 | 54 |
| paraphrase | 72 | 288 |
| multi-chunk | 36 | 144 |
| near-dup | 12 | 44 |
| distractor | 12 | 46 |
| reference | 48 | 192 |

## recall@k, MRR@k and complete@k by category

| Search path | k | Category | Questions | recall@k | MRR@k | complete@k |
|---|---|---|---|---|---|---|
| exact search (index scans off) | 5 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| exact search (index scans off) | 5 | paraphrase | 288 | 0.760 | 0.492 | 0.819 |
| exact search (index scans off) | 5 | multi-chunk | 144 | 0.626 | 0.733 | 0.347 |
| exact search (index scans off) | 5 | near-dup | 44 | 0.977 | 0.501 | 0.977 |
| exact search (index scans off) | 5 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| exact search (index scans off) | 5 | reference | 192 | 0.893 | 0.672 | 0.906 |
| exact search (index scans off) | 10 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| exact search (index scans off) | 10 | paraphrase | 288 | 0.891 | 0.507 | 0.927 |
| exact search (index scans off) | 10 | multi-chunk | 144 | 0.749 | 0.740 | 0.549 |
| exact search (index scans off) | 10 | near-dup | 44 | 1.000 | 0.504 | 1.000 |
| exact search (index scans off) | 10 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| exact search (index scans off) | 10 | reference | 192 | 0.974 | 0.682 | 0.979 |
| HNSW forced, iterative scan on | 5 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| HNSW forced, iterative scan on | 5 | paraphrase | 288 | 0.708 | 0.450 | 0.767 |
| HNSW forced, iterative scan on | 5 | multi-chunk | 144 | 0.630 | 0.746 | 0.347 |
| HNSW forced, iterative scan on | 5 | near-dup | 44 | 0.977 | 0.501 | 0.977 |
| HNSW forced, iterative scan on | 5 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| HNSW forced, iterative scan on | 5 | reference | 192 | 0.878 | 0.656 | 0.891 |
| HNSW forced, iterative scan on | 10 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| HNSW forced, iterative scan on | 10 | paraphrase | 288 | 0.828 | 0.463 | 0.865 |
| HNSW forced, iterative scan on | 10 | multi-chunk | 144 | 0.755 | 0.753 | 0.556 |
| HNSW forced, iterative scan on | 10 | near-dup | 44 | 1.000 | 0.504 | 1.000 |
| HNSW forced, iterative scan on | 10 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| HNSW forced, iterative scan on | 10 | reference | 192 | 0.958 | 0.666 | 0.964 |
| planner default, iterative scan on | 5 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| planner default, iterative scan on | 5 | paraphrase | 288 | 0.708 | 0.450 | 0.767 |
| planner default, iterative scan on | 5 | multi-chunk | 144 | 0.630 | 0.746 | 0.347 |
| planner default, iterative scan on | 5 | near-dup | 44 | 0.977 | 0.501 | 0.977 |
| planner default, iterative scan on | 5 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| planner default, iterative scan on | 5 | reference | 192 | 0.878 | 0.656 | 0.891 |
| planner default, iterative scan on | 10 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| planner default, iterative scan on | 10 | paraphrase | 288 | 0.891 | 0.507 | 0.927 |
| planner default, iterative scan on | 10 | multi-chunk | 144 | 0.749 | 0.740 | 0.549 |
| planner default, iterative scan on | 10 | near-dup | 44 | 1.000 | 0.504 | 1.000 |
| planner default, iterative scan on | 10 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| planner default, iterative scan on | 10 | reference | 192 | 0.974 | 0.682 | 0.979 |

## Above-role probes (answer exists, caller may not see it)

| Search path | k | Probes | Forbidden gold chunks returned | Probes with a full result set |
|---|---|---|---|---|
| exact search (index scans off) | 5 | 384 | 0 | 384 |
| exact search (index scans off) | 10 | 384 | 0 | 384 |
| HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| planner default, iterative scan on | 5 | 384 | 0 | 384 |
| planner default, iterative scan on | 10 | 384 | 0 | 384 |

## Index fallback

- Exact-scan fallback fired **0** time(s) during this run (the HNSW path returned fewer than k rows and the store repeated the search as an exact scan).
  - exact search (index scans off): 0
  - HNSW forced, iterative scan on: 0
  - planner default, iterative scan on: 0

## Caveats

- Embedding input is the raw chunk / question text; OllamaProvider adds no nomic task prefixes (search_document:/search_query:), so this measures the repo's provider as shipped.
- The hard corpus is synthetic and generated from hand-written topic templates with a fixed seed: it is harder than the standard corpus by construction, but it is still not real documents. Do not compare with public benchmarks.
- recall@k = |gold in top-k| / |gold| over ALL gold chunks (overlap duplicates of one answer sentence count separately, so this is strict); MRR@k = 1/rank of the first gold chunk in top-k, else 0; complete@k = share of questions whose every answer part has a gold chunk in top-k.
- Scored questions are those whose answer the caller may see. Above-role probes have no recall: the check is that the permitted set holds no answer and that no forbidden gold chunk is returned.
- Questions are scored per (tenant, role) caller against only the chunks that caller may see; the oracle asserts labels never point at forbidden chunks and that no non-gold chunk of the tenant carries the answer text.
- Single run per CI job, no confidence intervals. Per-category buckets are small (12 to 72 distinct questions); treat differences of a few points as noise.
