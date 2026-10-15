# Retrieval quality with real embeddings (Ollama)

Generated 2026-10-15 12:11:24Z, git `67f6887bed37f52fad14791a21cbd1c306a91c78`. Machine-readable copy: `quality-results-ollama-hard.json`.

**RETRIEVAL QUALITY with REAL embeddings (Ollama) for ingest and query, on the HARD synthetic corpus (near-duplicates across and within tenants, distractors, paraphrases, multi-chunk answers, above-role questions). Chat is the fake LLM. Synthetic and generated: read these numbers as 'this model on this corpus', not as a general claim.**

## Setup (stated with every number)

- Embedding model: `nomic-embed-text`, 768 dimensions, via Ollama 0.40.4. Ollama at localhost, CPU inference unless the host has a GPU; hardware below is the test runner's.
- Chat: Fake (deterministic; chat is not part of retrieval quality).
- Corpus: 2 tenants, 512 documents, 1910 base chunks (1828 distinct texts embedded in 270.8 s), seed 42; 4503 rows in `chunks` at measurement.
- Hostile rows: 2593 extra rows in 3 hostile tenants (fake hash vectors, never the target of a labelled question) are also in the chunks table, as in the leakage suite; they are invisible to the labelled callers under RLS.
- Labelled questions: 768 (192 distinct question texts).
- Chunking: fixed-window-chars, whitespace-snap, no trimming; size=500 chars; overlap=100 chars; wordSnapWindow=80 chars.
- Runner / hardware: GitHub Actions runner 'GitHub Actions 1000000822' (RUNNER_OS=Linux, ImageOS=ubuntu24); AMD EPYC 7763 64-Core Processor, 4 logical cores, 15.6 GiB RAM, Ubuntu 24.04.5 LTS, .NET 9.0.21.
- Database: PostgreSQL 16.15 (Debian 16.15-1.pgdg12+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14+deb12u1) 12.2.0, 64-bit, pgvector 0.8.7; on the same machine as the test runner (CI service container or local).

## recall@k and MRR@k

| Search path | k | Questions | recall@k | MRR@k |
|---|---|---|---|---|
| exact search (index scans off) | 5 | 768 | 0.812 | 0.625 |
| exact search (index scans off) | 10 | 768 | 0.905 | 0.635 |
| HNSW forced, iterative scan on | 5 | 768 | 0.799 | 0.619 |
| HNSW forced, iterative scan on | 10 | 768 | 0.890 | 0.628 |
| planner default, iterative scan on | 5 | 768 | 0.799 | 0.619 |
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
| HNSW forced, iterative scan on | 5 | paraphrase | 288 | 0.726 | 0.470 | 0.785 |
| HNSW forced, iterative scan on | 5 | multi-chunk | 144 | 0.626 | 0.744 | 0.347 |
| HNSW forced, iterative scan on | 5 | near-dup | 44 | 0.977 | 0.501 | 0.977 |
| HNSW forced, iterative scan on | 5 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| HNSW forced, iterative scan on | 5 | reference | 192 | 0.893 | 0.672 | 0.906 |
| HNSW forced, iterative scan on | 10 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| HNSW forced, iterative scan on | 10 | paraphrase | 288 | 0.845 | 0.483 | 0.882 |
| HNSW forced, iterative scan on | 10 | multi-chunk | 144 | 0.755 | 0.753 | 0.556 |
| HNSW forced, iterative scan on | 10 | near-dup | 44 | 1.000 | 0.504 | 1.000 |
| HNSW forced, iterative scan on | 10 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| HNSW forced, iterative scan on | 10 | reference | 192 | 0.974 | 0.682 | 0.979 |
| planner default, iterative scan on | 5 | keyword | 54 | 1.000 | 0.898 | 1.000 |
| planner default, iterative scan on | 5 | paraphrase | 288 | 0.726 | 0.470 | 0.785 |
| planner default, iterative scan on | 5 | multi-chunk | 144 | 0.626 | 0.744 | 0.347 |
| planner default, iterative scan on | 5 | near-dup | 44 | 0.977 | 0.501 | 0.977 |
| planner default, iterative scan on | 5 | distractor | 46 | 1.000 | 0.721 | 1.000 |
| planner default, iterative scan on | 5 | reference | 192 | 0.893 | 0.672 | 0.906 |
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

## Current-edition preference: before / after (same run)

Preference **off** is the previous behaviour. The others over-fetch k times the multiplier nearest PERMITTED candidates and re-order them with `distance + penalty` for chunks of a superseded document (penalty 2.0 or more is strict: every current candidate before any superseded one). The pool is produced by the unchanged tenant + role predicate and RLS, so the preference reorders and never admits a row; the 'above-role' and 'returned == min(k, visible)' assertions run for every preference.

- `off`: off
- `strict, pool 1xk`: pool = 1 x k, penalty 2
- `strict, pool 4xk`: pool = 4 x k, penalty 2
- `soft 0.05, pool 4xk`: pool = 4 x k, penalty 0.05
- `soft 0.10, pool 4xk`: pool = 4 x k, penalty 0.1

### exact search (index scans off), k = 5: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.052 | 0.898 / 1.000 / 1.000 / 0.052 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.492 / 0.760 / 0.819 / 0.226 | 0.568 / 0.760 / 0.819 / 0.226 | 0.585 / 0.807 / 0.865 / 0.001 | 0.585 / 0.807 / 0.865 / 0.036 | 0.585 / 0.807 / 0.865 / 0.001 |
| multi-chunk | 144 | 0.733 / 0.626 / 0.347 / 0.025 | 0.733 / 0.626 / 0.347 / 0.025 | 0.735 / 0.633 / 0.347 / 0.000 | 0.735 / 0.633 / 0.347 / 0.000 | 0.735 / 0.633 / 0.347 / 0.000 |
| near-dup | 44 | 0.501 / 0.977 / 0.977 / 0.795 | 0.977 / 0.977 / 0.977 / 0.795 | 0.985 / 1.000 / 1.000 / 0.005 | 0.985 / 1.000 / 1.000 / 0.755 | 0.985 / 1.000 / 1.000 / 0.436 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.672 / 0.893 / 0.906 / 0.150 | 0.709 / 0.893 / 0.906 / 0.150 | 0.736 / 0.956 / 0.969 / 0.004 | 0.734 / 0.951 / 0.964 / 0.043 | 0.736 / 0.956 / 0.969 / 0.004 |
| (all answerable) | 768 | 0.625 / 0.812 / 0.785 / 0.176 | 0.690 / 0.812 / 0.785 / 0.176 | 0.704 / 0.848 / 0.819 / 0.002 | 0.703 / 0.847 / 0.818 / 0.067 | 0.704 / 0.848 / 0.819 / 0.027 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.795 | 0.494 / 1.000 / 1.000 / 0.795 | 0.036 / 0.182 / 0.182 / 0.036 | 0.385 / 0.932 / 0.932 / 0.595 | 0.205 / 0.500 / 0.500 / 0.295 |

### exact search (index scans off), k = 10: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.133 | 0.898 / 1.000 / 1.000 / 0.133 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.013 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.507 / 0.891 / 0.927 / 0.248 | 0.594 / 0.891 / 0.927 / 0.248 | 0.597 / 0.927 / 0.948 / 0.000 | 0.597 / 0.927 / 0.948 / 0.049 | 0.597 / 0.927 / 0.948 / 0.011 |
| multi-chunk | 144 | 0.740 / 0.749 / 0.549 / 0.060 | 0.741 / 0.749 / 0.549 / 0.060 | 0.745 / 0.763 / 0.549 / 0.000 | 0.745 / 0.763 / 0.549 / 0.002 | 0.745 / 0.763 / 0.549 / 0.000 |
| near-dup | 44 | 0.504 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.000 | 0.985 / 1.000 / 1.000 / 0.407 | 0.985 / 1.000 / 1.000 / 0.384 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.682 / 0.974 / 0.979 / 0.188 | 0.737 / 0.974 / 0.979 / 0.188 | 0.740 / 0.995 / 0.995 / 0.000 | 0.739 / 0.995 / 0.995 / 0.076 | 0.740 / 0.995 / 0.995 / 0.022 |
| (all answerable) | 768 | 0.635 / 0.905 / 0.883 / 0.201 | 0.709 / 0.905 / 0.883 / 0.201 | 0.711 / 0.927 / 0.895 / 0.000 | 0.711 / 0.927 / 0.895 / 0.062 | 0.711 / 0.927 / 0.895 / 0.032 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.768 | 0.316 / 1.000 / 1.000 / 0.768 | 0.000 / 0.000 / 0.000 / 0.000 | 0.397 / 1.000 / 1.000 / 0.418 | 0.252 / 0.909 / 0.909 / 0.295 |

### HNSW forced, iterative scan on, k = 5: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.056 | 0.898 / 1.000 / 1.000 / 0.056 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.470 / 0.726 / 0.785 / 0.227 | 0.545 / 0.726 / 0.785 / 0.227 | 0.558 / 0.762 / 0.819 / 0.003 | 0.558 / 0.762 / 0.819 / 0.040 | 0.558 / 0.762 / 0.819 / 0.003 |
| multi-chunk | 144 | 0.744 / 0.626 / 0.347 / 0.033 | 0.744 / 0.626 / 0.347 / 0.033 | 0.751 / 0.637 / 0.347 / 0.000 | 0.751 / 0.637 / 0.347 / 0.000 | 0.751 / 0.637 / 0.347 / 0.000 |
| near-dup | 44 | 0.501 / 0.977 / 0.977 / 0.795 | 0.977 / 0.977 / 0.977 / 0.795 | 0.985 / 1.000 / 1.000 / 0.005 | 0.985 / 1.000 / 1.000 / 0.755 | 0.985 / 1.000 / 1.000 / 0.436 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.672 / 0.893 / 0.906 / 0.150 | 0.709 / 0.893 / 0.906 / 0.150 | 0.736 / 0.956 / 0.969 / 0.004 | 0.734 / 0.951 / 0.964 / 0.043 | 0.736 / 0.956 / 0.969 / 0.004 |
| (all answerable) | 768 | 0.619 / 0.799 / 0.772 / 0.178 | 0.684 / 0.799 / 0.772 / 0.178 | 0.697 / 0.832 / 0.802 / 0.003 | 0.696 / 0.830 / 0.801 / 0.069 | 0.697 / 0.832 / 0.802 / 0.027 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.795 | 0.494 / 1.000 / 1.000 / 0.795 | 0.036 / 0.182 / 0.182 / 0.036 | 0.385 / 0.932 / 0.932 / 0.595 | 0.205 / 0.500 / 0.500 / 0.295 |

### HNSW forced, iterative scan on, k = 10: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.130 | 0.898 / 1.000 / 1.000 / 0.130 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.013 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.483 / 0.845 / 0.882 / 0.251 | 0.566 / 0.845 / 0.882 / 0.251 | 0.570 / 0.884 / 0.903 / 0.000 | 0.570 / 0.884 / 0.903 / 0.051 | 0.570 / 0.884 / 0.903 / 0.012 |
| multi-chunk | 144 | 0.753 / 0.755 / 0.556 / 0.063 | 0.755 / 0.755 / 0.556 / 0.063 | 0.760 / 0.769 / 0.556 / 0.000 | 0.760 / 0.769 / 0.556 / 0.002 | 0.760 / 0.769 / 0.556 / 0.000 |
| near-dup | 44 | 0.504 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.000 | 0.985 / 1.000 / 1.000 / 0.407 | 0.985 / 1.000 / 1.000 / 0.384 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.682 / 0.974 / 0.979 / 0.188 | 0.737 / 0.974 / 0.979 / 0.188 | 0.740 / 0.995 / 0.995 / 0.000 | 0.739 / 0.995 / 0.995 / 0.077 | 0.740 / 0.995 / 0.995 / 0.022 |
| (all answerable) | 768 | 0.628 / 0.890 / 0.867 / 0.203 | 0.701 / 0.890 / 0.867 / 0.203 | 0.704 / 0.912 / 0.879 / 0.000 | 0.704 / 0.912 / 0.879 / 0.063 | 0.704 / 0.912 / 0.879 / 0.032 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.768 | 0.316 / 1.000 / 1.000 / 0.768 | 0.000 / 0.000 / 0.000 / 0.000 | 0.397 / 1.000 / 1.000 / 0.418 | 0.252 / 0.909 / 0.909 / 0.295 |

### planner default, iterative scan on, k = 5: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.056 | 0.898 / 1.000 / 1.000 / 0.056 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.470 / 0.726 / 0.785 / 0.227 | 0.545 / 0.726 / 0.785 / 0.227 | 0.585 / 0.807 / 0.865 / 0.001 | 0.585 / 0.807 / 0.865 / 0.036 | 0.585 / 0.807 / 0.865 / 0.001 |
| multi-chunk | 144 | 0.744 / 0.626 / 0.347 / 0.033 | 0.744 / 0.626 / 0.347 / 0.033 | 0.735 / 0.633 / 0.347 / 0.000 | 0.735 / 0.633 / 0.347 / 0.000 | 0.735 / 0.633 / 0.347 / 0.000 |
| near-dup | 44 | 0.501 / 0.977 / 0.977 / 0.795 | 0.977 / 0.977 / 0.977 / 0.795 | 0.985 / 1.000 / 1.000 / 0.005 | 0.985 / 1.000 / 1.000 / 0.755 | 0.985 / 1.000 / 1.000 / 0.436 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.672 / 0.893 / 0.906 / 0.150 | 0.709 / 0.893 / 0.906 / 0.150 | 0.736 / 0.956 / 0.969 / 0.004 | 0.734 / 0.951 / 0.964 / 0.043 | 0.736 / 0.956 / 0.969 / 0.004 |
| (all answerable) | 768 | 0.619 / 0.799 / 0.772 / 0.178 | 0.684 / 0.799 / 0.772 / 0.178 | 0.704 / 0.848 / 0.819 / 0.002 | 0.703 / 0.847 / 0.818 / 0.067 | 0.704 / 0.848 / 0.819 / 0.027 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.795 | 0.494 / 1.000 / 1.000 / 0.795 | 0.036 / 0.182 / 0.182 / 0.036 | 0.385 / 0.932 / 0.932 / 0.595 | 0.205 / 0.500 / 0.500 / 0.295 |

### planner default, iterative scan on, k = 10: MRR@k / recall@k / complete@k / superseded share of top-k

| Category | Questions | off | strict, pool 1xk | strict, pool 4xk | soft 0.05, pool 4xk | soft 0.10, pool 4xk |
|---|---|---|---|---|---|---|
| keyword | 54 | 0.898 / 1.000 / 1.000 / 0.133 | 0.898 / 1.000 / 1.000 / 0.133 | 0.898 / 1.000 / 1.000 / 0.000 | 0.898 / 1.000 / 1.000 / 0.013 | 0.898 / 1.000 / 1.000 / 0.000 |
| paraphrase | 288 | 0.507 / 0.891 / 0.927 / 0.248 | 0.594 / 0.891 / 0.927 / 0.248 | 0.597 / 0.927 / 0.948 / 0.000 | 0.597 / 0.927 / 0.948 / 0.049 | 0.597 / 0.927 / 0.948 / 0.011 |
| multi-chunk | 144 | 0.740 / 0.749 / 0.549 / 0.060 | 0.741 / 0.749 / 0.549 / 0.060 | 0.745 / 0.763 / 0.549 / 0.000 | 0.745 / 0.763 / 0.549 / 0.002 | 0.745 / 0.763 / 0.549 / 0.000 |
| near-dup | 44 | 0.504 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.700 | 0.985 / 1.000 / 1.000 / 0.000 | 0.985 / 1.000 / 1.000 / 0.407 | 0.985 / 1.000 / 1.000 / 0.384 |
| distractor | 46 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.007 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 | 0.721 / 1.000 / 1.000 / 0.000 |
| reference | 192 | 0.682 / 0.974 / 0.979 / 0.188 | 0.737 / 0.974 / 0.979 / 0.188 | 0.740 / 0.995 / 0.995 / 0.000 | 0.739 / 0.995 / 0.995 / 0.076 | 0.740 / 0.995 / 0.995 / 0.022 |
| (all answerable) | 768 | 0.635 / 0.905 / 0.883 / 0.201 | 0.709 / 0.905 / 0.883 / 0.201 | 0.711 / 0.927 / 0.895 / 0.000 | 0.711 / 0.927 / 0.895 / 0.062 | 0.711 / 0.927 / 0.895 / 0.032 |
| withdrawn-reachability | 44 | 0.780 / 1.000 / 1.000 / 0.768 | 0.316 / 1.000 / 1.000 / 0.768 | 0.000 / 0.000 / 0.000 / 0.000 | 0.397 / 1.000 / 1.000 / 0.418 | 0.252 / 0.909 / 0.909 / 0.295 |

### Above-role probes by preference

| Preference | Search path | k | Probes | Forbidden gold chunks returned | Probes with a full result set |
|---|---|---|---|---|---|
| off | exact search (index scans off) | 5 | 384 | 0 | 384 |
| off | exact search (index scans off) | 10 | 384 | 0 | 384 |
| strict, pool 1xk | exact search (index scans off) | 5 | 384 | 0 | 384 |
| strict, pool 1xk | exact search (index scans off) | 10 | 384 | 0 | 384 |
| strict, pool 4xk | exact search (index scans off) | 5 | 384 | 0 | 384 |
| strict, pool 4xk | exact search (index scans off) | 10 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | exact search (index scans off) | 5 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | exact search (index scans off) | 10 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | exact search (index scans off) | 5 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | exact search (index scans off) | 10 | 384 | 0 | 384 |
| off | HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| off | HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| strict, pool 1xk | HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| strict, pool 1xk | HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| strict, pool 4xk | HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| strict, pool 4xk | HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | HNSW forced, iterative scan on | 5 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | HNSW forced, iterative scan on | 10 | 384 | 0 | 384 |
| off | planner default, iterative scan on | 5 | 384 | 0 | 384 |
| off | planner default, iterative scan on | 10 | 384 | 0 | 384 |
| strict, pool 1xk | planner default, iterative scan on | 5 | 384 | 0 | 384 |
| strict, pool 1xk | planner default, iterative scan on | 10 | 384 | 0 | 384 |
| strict, pool 4xk | planner default, iterative scan on | 5 | 384 | 0 | 384 |
| strict, pool 4xk | planner default, iterative scan on | 10 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | planner default, iterative scan on | 5 | 384 | 0 | 384 |
| soft 0.05, pool 4xk | planner default, iterative scan on | 10 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | planner default, iterative scan on | 5 | 384 | 0 | 384 |
| soft 0.10, pool 4xk | planner default, iterative scan on | 10 | 384 | 0 | 384 |

Category `withdrawn-reachability` is an extra probe set built by this run (not in the corpus gold): the same topic question but asking about the withdrawn edition, gold = the fact chunk of any superseded edition. It measures whether withdrawn material is still reachable (demoted, not hidden); it does not measure which withdrawn edition is picked.

## Index fallback

- Exact-scan fallback fired **0** time(s) during this run (the HNSW path returned fewer than k rows and the store repeated the search as an exact scan).
  - exact search (index scans off): 0
  - HNSW forced, iterative scan on: 0
  - planner default, iterative scan on: 0
- By search path and preference:
  - exact search (index scans off) | off: 0
  - exact search (index scans off) | strict, pool 1xk: 0
  - exact search (index scans off) | strict, pool 4xk: 0
  - exact search (index scans off) | soft 0.05, pool 4xk: 0
  - exact search (index scans off) | soft 0.10, pool 4xk: 0
  - HNSW forced, iterative scan on | off: 0
  - HNSW forced, iterative scan on | strict, pool 1xk: 0
  - HNSW forced, iterative scan on | strict, pool 4xk: 0
  - HNSW forced, iterative scan on | soft 0.05, pool 4xk: 0
  - HNSW forced, iterative scan on | soft 0.10, pool 4xk: 0
  - planner default, iterative scan on | off: 0
  - planner default, iterative scan on | strict, pool 1xk: 0
  - planner default, iterative scan on | strict, pool 4xk: 0
  - planner default, iterative scan on | soft 0.05, pool 4xk: 0
  - planner default, iterative scan on | soft 0.10, pool 4xk: 0

## Caveats

- Embedding input is the raw chunk / question text; OllamaProvider adds no nomic task prefixes (search_document:/search_query:), so this measures the repo's provider as shipped.
- The hard corpus is synthetic and generated from hand-written topic templates with a fixed seed: it is harder than the standard corpus by construction, but it is still not real documents. Do not compare with public benchmarks.
- recall@k = |gold in top-k| / |gold| over ALL gold chunks (overlap duplicates of one answer sentence count separately, so this is strict); MRR@k = 1/rank of the first gold chunk in top-k, else 0; complete@k = share of questions whose every answer part has a gold chunk in top-k.
- Scored questions are those whose answer the caller may see. Above-role probes have no recall: the check is that the permitted set holds no answer and that no forbidden gold chunk is returned.
- Questions are scored per (tenant, role) caller against only the chunks that caller may see; the oracle asserts labels never point at forbidden chunks and that no non-gold chunk of the tenant carries the answer text.
- Single run per CI job, no confidence intervals. Per-category buckets are small (12 to 72 distinct questions); treat differences of a few points as noise.
