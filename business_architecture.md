# Business Architecture — Knovara

This document captures the business-level architecture for **Knovara** (knovara.net), an intelligent context retrieval and optimization engine: a provider-neutral knowledge processing and context optimization infrastructure for AI agents.

## Problem Statement

AI agents that reason over large information sources — codebases, books, PDFs, web resources — repeatedly re-read and re-process the same material within and across sessions. This wastes tokens, time, and money, and it is not solved by token budgets or embeddings alone. The agent needs a way to understand a source once, remember what it already discovered, and retrieve only the minimum evidence required for the task at hand.

## Mission

> Convert large, heterogeneous information sources into persistent, structured, searchable knowledge representations, so that an AI agent can retrieve the smallest sufficient context for a task without repeatedly processing the original source.

## Who Consumes This

Decided direction: a **universal, agent-independent infrastructure layer** — not a bespoke RAG pipeline bolted onto one product, and not limited to a single agent or tool. Any AI agent (coding assistant, research agent, support bot, document-analysis tool) should be able to call it as a service.

## Business Domains & Stakeholders

| Domain | Primary Stakeholder(s) | Description |
|--------|------------------------|-------------|
| **Platform / Core Engine** | Platform Engineers, Architects | Build and operate the ingestion, indexing, retrieval, and context-memory services that make up the engine. |
| **Agent Integrations** | AI Agent / Product Teams | Consume the engine's APIs so coding assistants, research agents, and support bots retrieve evidence instead of re-reading raw sources. |
| **Content Owners** | Repository Owners, Documentation Teams, Knowledge Base Owners | Provide and maintain the source material (codebases, books, PDFs, web resources) that gets ingested and kept in sync. |
| **Operations / DevOps** | SRE, Operations Engineers | Run ingestion pipelines, indexing jobs, cache infrastructure, and incremental re-indexing on changed sources. |
| **Evaluation & Quality** | ML/Platform QA | Measure token savings, latency, retrieval accuracy, and citation correctness against baselines. |
| **Management & Sponsors** | Executives, Project Sponsors | Track cost savings (token spend), adoption across agents, and ROI of the engine versus ad-hoc RAG per product. |

## Value Streams

1. **Ingestion → Knowledge Representation**
   Source material (code, documents, web pages) is parsed with format-aware processors and converted into hierarchical, structured knowledge (summaries, entities, relationships) — once, not per-query.
2. **Indexing → Multi-Index Storage**
   Knowledge is written into complementary indexes (semantic, lexical, graph, structural) so different question types can use the retrieval mechanism best suited to them.
3. **Query → Progressive Retrieval**
   An agent's question is analyzed, retrieval starts coarse (summaries/maps) and narrows only as needed, with a bounded search budget rather than indiscriminate context-stuffing.
4. **Evidence Reuse → Context Memory**
   Previously retrieved evidence, verified findings, and session knowledge are cached and reused across questions and sessions rather than rediscovered.
5. **Change → Incremental Update**
   When a source changes, only the affected nodes, summaries, and indexes are recomputed; unaffected knowledge is preserved, avoiding full reprocessing costs.
6. **Evaluation → Continuous Improvement**
   Token consumption, latency, recall, and accuracy are measured per task and compared against baseline/standard-RAG/advanced-RAG implementations to justify the engine's cost and to tune retrieval depth.

## Process Integration

- **Engine as a Service Layer**: exposes ingestion, retrieval, and context-memory as APIs/tools any agent can call, rather than being embedded inside one application.
- **Source of Truth**: the original document/codebase always remains authoritative; derived summaries and indexes are navigation/compression aids, never a replacement for the ability to retrieve original evidence.
- **Governance**: cache keys and indexes carry source version, model/embedding version, tenant, and access-permission metadata so agents never receive stale or unauthorized evidence.
- **Modularity**: embedding, reranking, and summarization providers are replaceable; the business is not locked to one LLM or vector-database vendor.

## Business Differentiators

1. **Read once, understand once, reuse many times** — knowledge persists beyond a single agent request or session.
2. **Retrieve progressively, not indiscriminately** — an agent receives more information only when the task requires it.
3. **Optimize context for reasoning, not just retrieval** — the goal is the smallest sufficient evidence package, not the largest relevant one.

## Metrics & KPIs

- **Token Efficiency**: input tokens consumed per completed task (target: materially below baseline "agent reads the raw source" and standard-RAG approaches).
- **Latency**: end-to-end time to answer.
- **Reasoning Overhead**: number of LLM calls per task.
- **Retrieval Quality**: Recall@K, answer accuracy, citation accuracy (answer is actually supported by the cited source).
- **Reuse Efficiency**: cache hit rate, reprocessing rate (how often an unchanged resource is processed again).
- **Adoption**: number of distinct agents/products integrated against the engine.

## Open Business Decisions (resolved direction, revisit if constraints change)

| Question | Options considered | Current preference |
|---|---|---|
| Who consumes this? | Universal infra / specialized document platform / integrated agent-orchestration feature | Universal, agent-independent infrastructure |
| How long should knowledge persist? | Session-only / cross-session / both | Both, with configurable retention policy per tenant/resource |
| Primary optimization target? | Tokens / latency / both | Both, jointly optimized |

---
*Business architecture drafted from the project brainstorming session (chat history.docx) on 2026-11-23.*
