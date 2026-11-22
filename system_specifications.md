# System Specifications — Intelligent Context Engine (ICRO)

This document specifies the **Intelligent Context Engine** (working name: **ICRO — Intelligent Context Retrieval and Optimization**), as defined in the project brainstorming session.

## Identity & Role

- **Name**: ICRO / Intelligent Context Engine
- **Type**: Provider-neutral knowledge processing and context optimization infrastructure for AI agents
- **Primary Role**: Transform large, heterogeneous information sources (codebases, books, PDFs, websites, documents) into persistent, structured, searchable, reusable knowledge, and deliver the minimum sufficient evidence an agent needs to reason correctly — without repeatedly reprocessing the original source.
- **Positioning**: An infrastructure capability consumed by any AI agent, not a single-product RAG feature.

## Problem This System Solves

| Problem | What happens today | What this system provides |
|---|---|---|
| Repeated reading | The same content is processed multiple times | Persistent indexing and caching |
| Excessive context | Too much irrelevant information enters the prompt | Query-aware, progressive retrieval |
| Lost relationships | A small extracted passage lacks its surrounding context | Hierarchical and contextual indexing |
| Poor retrieval | The agent retrieves the wrong sections and has to search again | Hybrid search and reranking |
| Repeated reasoning | The agent rediscovers conclusions already reached | Session knowledge and evidence memory |

## Functional Capabilities

1. **Format-aware ingestion** of code repositories, books, PDFs, websites, and general documents, with version detection.
2. **Hierarchical knowledge representation**: document → chapter/module → section/class → topic → original passage.
3. **Multi-dimensional indexing**: semantic (vector), lexical (BM25), relationship (graph), structural (hierarchy/metadata).
4. **Contextual retrieval**: chunks are enriched with document/section/topic context before embedding, to avoid orphaned passages.
5. **Hybrid search and reranking**: combine index results via Reciprocal Rank Fusion; apply cross-encoder reranking selectively to candidate sets.
6. **Progressive, query-planned retrieval**: coarse-to-fine retrieval with an evidence-sufficiency check and a bounded search budget, rather than a uniform pipeline for every question.
7. **Context Memory & Reuse**: document knowledge cache, retrieval/evidence cache, and session knowledge memory, each independently queryable and reusable across agents/sessions.
8. **Incremental indexing and invalidation**: reprocess only changed resources; invalidate only dependent cached evidence; cache keys carry source version, model/index version, tenant, and access permissions.
9. **Evidence-grounded answers**: every answer must be traceable to, and verifiable against, the original source passage — summaries never stand in as the sole evidence for a factual claim.
10. **Agent-facing tools/APIs**: an agent should be able to locate, inspect, expand, compare, or verify evidence through API calls rather than downloading and reading an entire resource.

## Services & Responsibilities

| Service | Purpose |
|---|---|
| Resource Ingestion Service | Connectors, source synchronization, version detection, format-specific extraction |
| Knowledge Representation Service | Document hierarchy, semantic units, summaries, entities, relationships |
| Index Management Service | Vector, keyword, graph, and metadata indexing |
| Intelligent Retrieval Service | Query planning, multi-index retrieval, ranking, progressive context expansion |
| Context Optimization Service | Evidence selection, token budgeting, deduplication, compression, context construction |
| Context Memory Service | Session knowledge, reusable evidence, retrieval caches, invalidation |
| Evaluation & Observability Service | Accuracy, token consumption, latency, retrieval quality, cost measurement |

## Session Knowledge Memory — Example Shape

```json
{
  "resource": "payment-service",
  "facts": [
    {
      "claim": "Retry configuration is maintained in RetryPolicy",
      "source": "Payment/RetryPolicy.cs",
      "status": "verified"
    },
    {
      "claim": "PaymentService invokes RetryPolicy",
      "source": "Payment/PaymentService.cs",
      "status": "verified"
    }
  ],
  "previously_examined": ["PaymentService", "RetryPolicy", "PaymentStatus"],
  "unresolved_questions": ["Maximum retry interval"]
}
```

## Cache / Memory Taxonomy

| Mechanism | What it remembers |
|---|---|
| Document cache | What the document/codebase contains |
| Retrieval cache | Which passages were retrieved for a given query |
| Evidence memory | What was established as fact, and where the evidence came from |
| Session memory | What the agent has already investigated and what remains unresolved |
| Prompt cache (model-side) | Computation reuse for an identical, reusable prompt prefix — complementary, not a substitute for the above |

## Non-Functional Requirements / Design Constraints

- **Source fidelity**: the original source is always recoverable; derived representations (summaries, embeddings, graph edges) never replace it as evidence.
- **Chunking strategy**: structural boundaries (function, subsection, table) first; fixed-size token chunking only as a fallback.
- **Graph usage**: explicit relationships in relational storage first; a dedicated graph database is introduced only once complexity/reuse justifies the cost.
- **Cost-aware indexing**: three investment levels (Basic / Enhanced / Advanced) so processing cost scales with resource usage/frequency, not applied uniformly.
- **Provider neutrality**: embedding, reranking, and summarization models are swappable via adapters; model/embedding versions are tracked so incompatible indexes are never silently mixed.
- **Multi-tenancy & security**: cache and index entries carry tenant and access-permission metadata to prevent cross-tenant or unauthorized evidence leakage.
- **Modularity over day-one completeness**: start on PostgreSQL (metadata + hierarchy + full-text + vector); add OpenSearch/Neo4j/dedicated vector DB only when scale/benchmarks justify it.

## Delivery Roadmap

| Phase | Scope | Deliverable |
|---|---|---|
| **Phase 1 — Intelligent document ingestion** | PDF, Markdown, HTML, DOCX; structural parsing, hierarchical representation, chunking, metadata extraction | A reusable document knowledge store |
| **Phase 2 — Hybrid retrieval** | Embeddings, BM25, contextual retrieval, query analysis, reranking | Agents retrieve relevant evidence instead of reading entire resources |
| **Phase 3 — Context memory** | Evidence memory, retrieval caching, session knowledge, source-version-aware invalidation | Agents reuse previously discovered information |
| **Phase 4 — Code intelligence** | AST parsing, symbol indexing, dependency analysis, incremental codebase indexing | Specialized codebase intelligence |
| **Phase 5 — Advanced knowledge reasoning** | Graph-based retrieval, cross-document reasoning, adaptive indexing, intelligent context-budget allocation | A sophisticated knowledge and context optimization platform |

## Evaluation Plan

**Metrics**

| Metric | What it measures |
|---|---|
| Input tokens per task | Token efficiency |
| Time to answer | End-to-end latency |
| Number of LLM calls | Reasoning/retrieval overhead |
| Retrieval Recall@K | Whether relevant evidence is found |
| Answer accuracy | Whether the final response is correct |
| Citation accuracy | Whether the answer is actually supported by the cited source |
| Cache hit rate | How much previous work is reused |
| Reprocessing rate | How often unchanged resources are processed again |

**Comparison baselines**: (1) agent reads raw source, (2) standard RAG (chunking + embeddings), (3) advanced RAG (hierarchical indexing + hybrid retrieval + reranking), (4) ICRO (advanced retrieval + persistent knowledge + evidence memory + context optimization). Run identical question sets against codebases, books, and web resources, including repeated-question and changed-source scenarios. Primary measure: tokens and time **per successfully completed task**, not chunk count retrieved.

## Open Decisions (resolved direction)

1. **Consumers**: universal, agent-independent infrastructure (not a single specialized platform or an orchestration-feature bolt-on).
2. **Persistence**: both session-scoped and cross-session/cross-agent knowledge, with configurable retention policy.
3. **Optimization target**: jointly optimize token consumption and response latency.

---
*Specifications drafted from the project brainstorming session (chat history.docx) on 2026-11-23.*
