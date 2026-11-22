# System Architecture — Intelligent Context Engine (ICRO)

This document describes the technical architecture of **ICRO**, a multi-stage pipeline that converts large information sources (codebases, books, PDFs, web resources) into persistent, structured knowledge and serves AI agents the minimum sufficient evidence for a given task.

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

## Core Components

| Component | Description |
|-----------|-------------|
| **Resource Ingestion Service** | Connectors for codebases, documents, and web sources; detects source versions (hash/timestamp/HTTP validators) and triggers re-ingestion only for changed material. |
| **Knowledge Representation Service** | Builds document hierarchy, semantic units, multi-level summaries, entities, and relationships from parsed structure. |
| **Index Management Service** | Maintains the four index types (vector, keyword, graph, metadata) and keeps them in sync with the knowledge representation. |
| **Intelligent Retrieval Service** | Plans queries, executes multi-index retrieval, reranks candidates, and progressively expands context only as needed. |
| **Context Optimization Service** | Deduplicates evidence, applies token budgeting, selects the minimum sufficient evidence set, and assembles the final context package. |
| **Context Memory Service** | Owns the document knowledge cache, retrieval/evidence cache, and session knowledge memory; enforces cache invalidation tied to source version. |
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

Cache keys include source version, index/model version, tenant, and access permissions, so an agent can never receive stale or unauthorized evidence.

## Progressive / Intelligent Retrieval

Retrieval is query-planned, not uniform:

```
User question
  → Step 1: Query understanding (subject, answer type, likely sources)
  → Step 2: Coarse retrieval (summaries, topic maps, relevant symbols)
  → Step 3: Targeted retrieval (specific passages/logic/config)
  → Step 4: Context expansion (neighbors, callers, docs) — only if evidence requires it
  → Step 5: Evidence sufficiency check
       sufficient  → construct answer with source references
       insufficient → retrieve missing evidence under a bounded search budget
```

Retrieval depth and token budget scale with task type: a factual lookup may need one passage; a code-impact analysis may need multi-file dependency traversal; a whole-book analysis may need chapter-level evidence.

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
| Original resource storage | S3-compatible object storage |
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

---
*System architecture drafted from the project brainstorming session (chat history.docx) on 2026-11-23.*
