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

## Business Model (decided 2026-11-25)

> The full policy (tiers, community commitments, pricing principles, metering, revenue path, risks, decision log) is in
> `business_model_and_monetization_policy.md`. This section is the summary.

| Question | Decision |
|---|---|
| First paying customer | **Engineering teams and small companies** already using AI coding assistants who want a shared knowledge base with roles and audit. Individual developers adopt the free tier first (bottom-up). |
| What is paid | **Open core.** The local runtime, CLI and MCP server are free and open source; team and enterprise capabilities and support are paid. |
| Open-source licence | **Apache-2.0** for the open-source runtime. Revenue is protected by proprietary team/enterprise modules and managed services, not by the licence. |
| "No paid third-party services" | Applies to the **product**: the runtime and its models must never need a paid external service. Ordinary business tooling (payment processing, hosting, domain, email) is allowed to run the company and the managed offering. |
| Control plane boundary (decided 2026-11-25) | **Open authority, paid governance.** One customer's authority domain is open under Apache-2.0; governance modules, the multi-customer operator layer, hosting and support are paid. See "Open / proprietary boundary" below. |
| Contributor terms (decided 2026-11-25) | **DCO sign-off** on every commit to the open repository. No CLA, because the proprietary modules are separate code we write ourselves. |

### Offering (tentative; prices are not set)

| Tier | Contents | Pays |
|---|---|---|
| **Community** | Local runtime, CLI, MCP server, desktop UI with no account, plus the open server mode and authority domain a team can **self-host** (roles, enrollment, device tokens, installer hosting) | Free, open source |
| **Team (hosted)** | We host and operate your team server and authority domain: backups, upgrades, monitoring, support. The same software self-hosted stays free | Per seat or registered runtime |
| **Enterprise** | Governance modules (SSO / SCIM, audit retention and export, policy engine, federation manager), run by the customer or hosted, with support and SLA | Annual contract |
| **Managed compute** | Our workers for OCR and bulk embedding; hosted team servers | Usage |

Why this shape: a local runtime costs us almost nothing per user, so the free tier can be generous, and local use cannot be metered without telemetry.
What can be charged for is whatever has an organization, a server or compute cost behind it. Prices need customer discovery; none are proposed here.

### Open / proprietary boundary (decided 2026-11-25)

The line follows two rules: **open what a customer must control to stay independent** (the plumbing of its own authority domain), and **charge for what costs us
operations or what enterprises require** (hosting, governance, support).

| Component | Licence |
|---|---|
| Engine, workers, MCP server, CLI, desktop app, local runtime and server mode | Open (Apache-2.0) |
| Token verification, device client library (`Knovara.Common/Device`), enrollment and token API specification | Open |
| One customer's authority domain: accounts, roles, tenants and device limits, enrollment codes, runtime registrations, token issuing, installer hosting, basic dashboard (today's `Knovara.ControlPlane`) | **Open** |
| SSO / SCIM, audit retention and export, policy engine (classification, residency, retention), federation manager | Proprietary (Enterprise) |
| Multi-customer operator layer: provisioning, usage metering, billing, managed operations | Proprietary |
| Hosting and support | Paid service |

Why this line: the open part is infrastructure plumbing that customers must be able to run, inspect and replace, which is what the target architecture's
"customer authority independent of Knovara's commercial infrastructure" requires. The proprietary part is exactly what a competitor would have to rebuild to
offer a rival hosted service. It also matches the pattern that works for self-hosted infrastructure tools: user management is free; enterprise authentication,
audit and hosting are paid.

**Trade-off accepted:** a team can self-host roles and enrollment for free, so the Team tier is mainly convenience (we run it) and support, not a feature unlock.
Revenue concentrates in managed hosting and in Enterprise governance, so the Enterprise modules and the hosting layer are where product effort pays back.

### Consequences for the architecture

- The Community tier must work with **no account and no network** (ADR-001 in `system_architecture.md`); paid features may require an account without affecting that.
- Everything in the repository today is open, so **no repository split and no history rewrite are needed now**. The Apache-2.0 `LICENSE`, a `NOTICE` and a DCO-based
  `CONTRIBUTING.md` can be added; this is a public legal step and waits for explicit confirmation.
- Proprietary modules live in a separate private repository (name to be chosen) and attach to the open control plane through **extension points** that do not exist yet:
  an audit sink, an identity provider (for SSO/SCIM), a policy evaluator and an operator/provisioning interface. Designing those seams is the first control-plane work.
- The open control plane is reshaped as one customer's authority domain (ADR-001); the multi-tenant operator behaviour it has today moves to the proprietary operator layer.
- Registered runtimes (today's enrolled devices) remain the natural pricing unit for the hosted Team tier.

### Open items

1. **Prove the value claim.** The differentiator is fewer tokens per completed task than raw reading or standard RAG. Current benchmarks measure retrieval quality
   (recall, MRR), not end-to-end tokens against a baseline. An end-to-end comparison must exist before pricing is set.
2. Trademark protection for the Knovara name, since the code is permissively licensed and the name is the remaining thing a hosting competitor cannot copy.
3. Telemetry policy (opt-in anonymous usage metrics) and what the hosted Team tier reports for metering.
4. Pricing, value metric and packaging, after talking to first teams.

---
*Business architecture drafted from the project brainstorming session (chat history.docx) on 2026-11-23.*
