# System Architecture — Knovara

This document describes the technical architecture of **Knovara**, a multi-stage pipeline that converts large information sources (codebases, books, PDFs, web resources) into persistent, structured knowledge and serves AI agents the minimum sufficient evidence for a given task.

## Design Principle

The original source is always the source of truth. Every derived artifact (summary, embedding, graph edge) exists to make access to that source faster — never to replace the ability to retrieve and inspect the original passage.

## Target Architecture (Pipeline Overview)

```
1. Source Ingestion
   Code repositories · Books · PDFs · Websites · Documents
        │
2. Format-Aware Processing
   Parsing · Structure extraction · Metadata · Version detection
        │
3. Knowledge Representation Engine
   Hierarchical summaries · Semantic units · Entities · Relationships
        │
4. Multi-Index Storage
   Vector index (semantic) · Keyword index (BM25) · Graph index (relationships) · Metadata index (filtering/location)
        │
5. Intelligent Retrieval Engine
   Query analysis · Search · Reranking · Context expansion
        │
6. Context Optimization Engine
   Deduplication · Token budgeting · Evidence selection · Context caching
        │
7. AI Agent / LLM
   Reasons using relevant, source-grounded information
```

Running alongside this pipeline: a **Context Memory & Reuse Engine** that persists previously retrieved evidence, verified findings, source versions, and reusable intermediate results.

## Two Paths, Two Kinds of Savings

The pipeline above is really two paths that run at different times. A question never passes through stages 1–4.

| Path | Runs | Stages | Where the saving comes from |
|---|---|---|---|
| **Ingest path** | Once per source *version* | 1 → 4 | Stage 1 compares the source's content hash with the last recorded version and stops immediately if nothing changed — no parsing, summarizing or embedding is repeated. |
| **Query path** | Once per *question* | Agent → 5 → 6 → 7 | Stage 5 checks the evidence cache before searching any index; a valid hit skips coarse, targeted and expansion search entirely. |

The ingest path makes knowledge cheap to keep; the query path makes repeat questions cheap to answer. They are measured separately (reprocessing rate vs. cache hit rate).

## External Dependencies

Model providers are outside the system boundary and are reached only through provider-neutral adapters:

| Provider | Used by | Purpose |
|---|---|---|
| Summarization LLM | Knowledge Representation | Section/chapter/document summaries |
| Embedding model | Index Management | Semantic (vector) index |
| Reranker (cross-encoder) | Intelligent Retrieval | Reranking the candidate set |

Every call is metered (tokens, latency, cost) by the Evaluation & Observability Service, since these calls are the main cost the system exists to reduce.

## Core Components

| Component | Description |
|-----------|-------------|
| **Access Control** | Authenticates every caller and derives the tenant from the credential (never from the request body); every store read and cache lookup is filtered by that tenant. |
| **Resource Ingestion Service** | Connectors for codebases, documents, and web sources; registers each resource once per (tenant, URI), detects source versions (hash/timestamp/HTTP validators) and triggers re-ingestion only for changed material. |
| **Knowledge Representation Service** | Builds document hierarchy, semantic units, multi-level summaries, entities, and relationships from parsed structure. |
| **Index Management Service** | Maintains the four index types (vector, keyword, graph, metadata) and keeps them in sync with the knowledge representation. |
| **Intelligent Retrieval Service** | Plans queries, executes multi-index retrieval, reranks candidates, and progressively expands context only as needed. |
| **Context Optimization Service** | Deduplicates evidence, applies token budgeting, selects the minimum sufficient evidence set, and assembles the final context package. |
| **Context Memory Service** | Owns the document knowledge cache, retrieval/evidence cache, and session knowledge memory; purges entries for superseded source versions. |
| **Model Provider Adapters** | Provider-neutral wrappers for the summarization LLM, embedding model and reranker; record model version and per-call cost. |
| **Evaluation & Observability Service** | Tracks tokens, latency, LLM call count, recall, accuracy, citation correctness, and cache hit/reprocessing rates. |

## Format-Aware Processing

| Source type | Approach | Key extraction |
|---|---|---|
| **Codebases** | AST parsing (Tree-sitter), supplemented with language-server/compiler semantic analysis for cross-file references | Classes, methods, interfaces, properties, signatures, imports/dependencies, call graphs, inheritance, file/project-level dependencies |
| **Books / PDFs / documents** | Structural extraction (e.g., Docling: PDF, DOCX, PPTX, EPUB, HTML) | Table of contents, chapter hierarchy, sections/subsections, paragraphs, page numbers, tables, figures, captions, equations, references |
| **Websites** | Meaningful-content extraction, not full-HTML indexing | URL/title, headings/sections, tables, code blocks, internal/external links, publication/retrieval timestamps |

Structural boundaries (functions, subsections, tables) are the primary chunking unit — fixed-size token chunking is a fallback, not the default strategy.

## Hierarchical Knowledge Representation

Each ingested source is represented at multiple levels of compression, so retrieval can start coarse and drill down:

```
Document-level representation (what is this source about?)
  └─ Chapter / module summaries
       └─ Section / class summaries
            └─ Topic summaries
                 └─ Original passages, tables, code — exact source, retrievable on demand
```

Summaries are navigation/compression layers only. Any factual answer must be traceable back to, and verifiable against, the original passage.

## Multi-Dimensional Indexing

| Index | Technology/technique | Best use |
|---|---|---|
| **Semantic** | Embeddings + vector database | Conceptually similar content |
| **Lexical** | BM25 / full-text search | Exact terminology, identifiers, phrases |
| **Relationship** | Knowledge graph | Dependencies and multi-hop connections |
| **Structural** | Hierarchy + metadata | Chapter, page, class, file, section lookups |

Hybrid retrieval combines results across indexes using **Reciprocal Rank Fusion (RRF)**; **contextual retrieval** (prepending document/chapter/section/topic context to a chunk before embedding) combined with BM25 and reranking substantially improves retrieval accuracy over embeddings alone. Reranking (cross-encoder) is applied selectively — to the candidate set, not the full corpus — due to latency cost.

## Context Memory & Reuse Engine

Operates at three levels, distinct from model-side prompt caching:

| Layer | What it remembers |
|---|---|
| **Document knowledge cache** | The document's structural representation, summaries/topic maps, extracted entities/relationships, embeddings, keyword indexes, and references to original passages — built once, reused by any session/agent. |
| **Retrieval & evidence cache** | Which passages were retrieved for a given query, and the relationships discovered among them, so a related follow-up question reuses prior evidence instead of re-searching from scratch. |
| **Session knowledge memory** | A structured, per-task record of claims, their source, and verification status, plus what has already been examined and what remains unresolved — a reusable knowledge state for the current task. |

### Cache keys

The two version-sensitive caches need different keys, because a question is not tied to any resource until retrieval has run:

| Cache | Key | Why |
|---|---|---|
| Document knowledge cache | `tenant + resource + source version + index version + model version` | Built after ingestion, when the resource and its version are known. |
| Evidence cache | `tenant + normalized query fingerprint + index version` | Looked up *before* retrieval, when only the question is known. |
| Session knowledge | `tenant + session id` | Not version-scoped; it records what a task has learned. |

An evidence-cache hit is **validated before use**: each cached evidence item carries the source version it was read from, and retrieval compares it with the current version in the Resource & Knowledge Store. If any cited source has a newer version, the hit is treated as a miss.

### Relationship to the Resource & Knowledge Store

The Resource & Knowledge Store (Postgres) is the durable record of every resource's hierarchy, summaries and original passages. The document knowledge cache (Redis) is a hot, disposable copy of the most-used parts of it. The cache can always be rebuilt from the store; the store is never rebuilt from the cache.

### Invalidation

When the Resource Ingestion Service records a new version of a resource, it emits a **version superseded** event carrying the old version. The Index Management Service replaces that resource's index entries, and the Context Memory Service purges document-cache entries keyed by the old version. Evidence-cache entries that cite the old version are not searched for; they fail validation on their next hit and are evicted then, with a TTL as the backstop against unbounded growth.

## Progressive / Intelligent Retrieval

Retrieval is query-planned, not uniform:

```
User question (authenticated; tenant from credential)
  → Step 0: Evidence cache lookup (tenant + query fingerprint + index version)
       valid hit → go to Step 5
  → Step 1: Query understanding (subject, answer type, likely sources)
  → Step 2: Coarse retrieval (summaries, topic maps, relevant symbols)
  → Step 3: Targeted retrieval (specific passages/logic/config)
  → Step 4: Context expansion (neighbors, callers, docs) — only if evidence requires it
  → Step 5: Evidence sufficiency check
       sufficient  → Step 6
       insufficient → Step 4 again, under a bounded search budget
  → Step 6: Verify against original passages
       read each cited passage's original text and current version from the
       Resource & Knowledge Store; drop evidence that no longer matches
```

Step 6 is what makes the design principle enforceable: summaries help find evidence, but what reaches the agent is always the original passage, at its current version.

Retrieval depth and token budget scale with task type: a factual lookup may need one passage; a code-impact analysis may need multi-file dependency traversal; a whole-book analysis may need chapter-level evidence.

## Retrieval Strategy by Data Size

Which retrieval pipeline runs depends on how many passages a search has to look through. Because every search is tenant-filtered, that is a **per-tenant** count: retrieval quality depends on it, while infrastructure cost depends on the total across all tenants. Build tier S1 well first, keep the tier boundaries configurable, and move a tenant up a tier only when its data and the scaling benchmark justify it.

Why size changes the answer:

- **Single-vector embeddings lose precision as the index grows.** Each passage is compressed into one fixed-size vector, so the more passages there are, the more unrelated ones land close to a query by chance; low-dimensional vectors degrade fastest (Reimers & Gurevych, 2021, *The Curse of Dense Low-Dimensional Information Retrieval for Large Index Sizes*; Weller et al., 2025, *On the Theoretical Limitations of Embedding-Based Retrieval*). They remain the best tool for paraphrase and meaning matches.
- **Keyword search (BM25) keeps its precision** on exact names, identifiers, codes and numbers at any scale, but never fixes vocabulary mismatch.
- **A cross-encoder reranker** reads question and passage together, so it has no compression bottleneck. Its cost depends on the number of candidates, not the corpus size, which makes it more valuable as the first stage gets noisier — but it can only reorder what the first stage found.

### Tiers

| Tier | Passages per search | Roughly | Candidate generation | Rerank | Infrastructure |
|---|---|---|---|---|---|
| **S0 Tiny** | under 1,000 | up to ~20–50 documents | Exact (brute-force) semantic search; keyword search as backup | Only when unsure, top 10 | Postgres + pgvector, no tuning |
| **S1 Small** | 1k–100k | up to a few thousand documents | Semantic top 20 + keyword top 10, merged | Always, ~30 candidates in batches of 8 | Postgres + pgvector HNSW + full-text search; model service on CPU |
| **S2 Medium** | 100k–10M | up to a few hundred thousand documents | Coarse retrieval first (document/section summaries), then hybrid within the matches; true BM25 | Always, 30–50 candidates | pgvector `halfvec`, partitioned per tenant; BM25 extension; Redis query cache; more ingestion workers |
| **S3 Large** | over 10M | millions of documents | Routing + compressed (quantized) vector search with full-precision re-scoring + BM25 or learned sparse (SPLADE) | Always, 30–50; self-owned GPU if traffic is high | Dedicated open-source engines (Qdrant or Milvus, OpenSearch), sharding |

Vector memory at 384 dimensions is about 1.5 KB per passage: ~150 MB at 100k passages, ~15 GB at 10M (~7.5 GB with `halfvec`), ~150 GB at 100M (~5 GB with binary quantization plus re-scoring). Index structures add to these figures. A tenant whose entire content fits in the agent's token budget can skip retrieval and receive all of it, if the longer generation time is acceptable.

### Measured on the current setup

Phase 2 benchmark (`eval/run_benchmark.py`): 86 passages, 40 questions, bge-small-en-v1.5 embeddings, ms-marco-MiniLM-L-6-v2 reranker, CPU only.

| Pipeline | R@3 | MRR | p50 latency |
|---|---|---|---|
| Lexical only | 0.68 | 0.53 | 7 ms |
| Semantic only | 0.90 | 0.82 | 21 ms |
| Hybrid (RRF) | 0.80 | 0.74 | 24 ms |
| Hybrid (RRF, ~60 candidates, one batch) + rerank | 0.90 | 0.84 | 6,046 ms |
| Semantic top 20 + rerank (batches of 8, sorted by length) | 0.93 | 0.85 | ~1,000 ms rerank step (offline measurement) |

- RRF blends rankings without reading the passages; at this size it ranked worse than semantic search alone. When a rerank follows, merge the candidate pools and let the reranker order them; use RRF only on paths without a rerank.
- Reranking ~60 candidates in one batch exhausted the model service's 2.2 GB memory limit. Batches of 8 sorted by length kept peak memory near 450 MB; more ONNX threads (2 → 8) did not reduce latency.
- With 40 questions, a 0.03 difference is one question — within noise. Tier boundaries must come from the scaling test below, not from these numbers.

### Choosing beyond size

1. **Latency budget.** Interactive chat (retrieval under ~300 ms): semantic search, reranking only when unsure (top 10 measured ~0.3 s). Agents and reports (1–2 s acceptable): always rerank.
2. **Content type.** Identifier-heavy content (invoices, logs, product codes, legal citations) needs a larger keyword share of the candidate pool; conversational text can lean on semantic search.
3. **Traffic.** Reranking 20 candidates takes ~0.8 s on the current hardware, so sustained traffic of N queries per second needs roughly N rerank workers (estimate).
4. **Ingestion volume.** Embedding cost grows with total data; large imports need more worker replicas or a self-owned GPU.

### Automatic selection

- **Tier per tenant:** the passage count is recorded when ingestion commits a version and mapped to S0–S3 by thresholds held in configuration, so boundaries can be tuned without code changes.
- **Escalation per query** (Steps 2–5 of Progressive Retrieval above): run the tier's cheapest step first and escalate only when the result looks uncertain — a low top score, or the top two results scoring too close — from semantic search, to adding keyword candidates, to reranking. The benchmark calibrates these thresholds.
- **Observability:** the Evaluation & Observability Service records which path each query took and its latency, so thresholds can be tuned from real traffic.

### Calibrating the boundaries

The tier boundaries above are rules of thumb. A **distractor scaling test** replaces them with measured values:

1. Keep a question set with known answers, expanded to 200+ questions.
2. Add growing amounts of unrelated passages from free public datasets (e.g. Wikipedia, BEIR corpora): 1k, 10k, 100k, then 1M.
3. At each size, measure R@3, MRR, latency and memory for every retrieval mode.
4. Where semantic-only starts losing to hybrid + rerank is the S0/S1 boundary; where pgvector latency or memory stops being acceptable is the S2/S3 boundary.

### Rollout

1. **Now (S0–S1):** merged semantic + keyword candidates with batched, length-sorted reranking; per-query escalation; tier configuration; question set expanded to 200+.
2. **Scaling test** up to 1M passages; set the thresholds from its results.
3. **S2 features** once any tenant passes ~100k passages: coarse retrieval over document/section summaries (requires summaries at ingestion), true BM25, `halfvec`, query cache.
4. **S3 infrastructure** only when a tenant approaches ~10M passages: dedicated engines, quantization, sharding.

## Incremental Indexing & Invalidation

- Detect changed resources via content hashes, timestamps, or repository/webhook events.
- Reprocess only changed resources; update affected structural nodes and indexes; recompute summaries only for affected parent sections; invalidate only dependent cached evidence.
- Tree-sitter's incremental parsing is used for codebases; content hashes for static documents; HTTP validators/freshness policies for websites.

## Indexing Investment Levels

| Level | Processing | When to use |
|---|---|---|
| **Basic** | Parsing, metadata, lightweight indexing | New or rarely used resources |
| **Enhanced** | Embeddings, summaries, hybrid search | Frequently accessed resources |
| **Advanced** | Relationship graphs, deep contextualization | Complex or heavily reused resources |

## Recommended Technology Stack

| Component | Recommended technology |
|---|---|
| Core orchestration | .NET 8/9 Web API |
| Document processing | Python workers, Docling |
| Code parsing | Tree-sitter |
| Semantic indexing | pgvector (initial), dedicated vector DB later if justified |
| Keyword search | PostgreSQL full-text search (initial) |
| Advanced hybrid search | OpenSearch (when scale justifies) |
| Knowledge relationships | PostgreSQL graph tables (initial), Neo4j (when justified) |
| Cache | Redis |
| Original resource storage | `knowledge_nodes.original_content` in Postgres (initial); S3-compatible object storage once sources outgrow it |
| Asynchronous processing | RabbitMQ or Kafka |
| Embedding models | Provider-neutral embedding adapter |
| Reranking | Cross-encoder reranker |
| Summarization | Cost-effective LLM with batch processing |
| Observability | OpenTelemetry + token/cost metrics |

Guiding rule: do not deploy every component on day one. PostgreSQL alone (metadata + hierarchy + full-text + vector) can carry the first version; OpenSearch/Neo4j/dedicated vector DB are introduced only once scale and retrieval benchmarks justify them.

## Architectural Cautions

1. Compression is not intelligence — the system must always be able to recover original evidence; a summary can omit an exception, a number, or a condition.
2. Do not use fixed-size chunking universally — prefer structural boundaries (function, subsection, table) and apply token limits only where necessary.
3. Do not build a large knowledge graph immediately — start with explicit relationships in relational storage; graphs are valuable but costly to construct/maintain.
4. Track indexing cost against resource usage — use the Basic/Enhanced/Advanced levels to avoid over-investing in rarely used material.
5. Keep model/embedding/reranking providers swappable, but version them explicitly so incompatible indexes are never silently mixed.
6. Never trust a tenant id supplied in a request body — derive it from the authenticated credential and filter every read by it.

---
*System architecture drafted from the project brainstorming session (chat history.docx) on 2026-11-23.*
