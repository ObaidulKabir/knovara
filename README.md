# ICE — Intelligent Context Engine (ICRO)

Application infrastructure for the Intelligent Context Engine described in
[business_architecture.md](business_architecture.md), [system_architecture.md](system_architecture.md),
and [system_specifications.md](system_specifications.md). This is a **Phase 1 scaffold**:
the pipeline, service boundaries, and infra are wired end-to-end, but most
services use simple in-memory placeholder logic rather than the full
Postgres/Redis/Docling/Tree-sitter implementations described in the docs —
see the comment at the top of each class for what to replace.

## Layout

```
src/
  ICE.Api                      Orchestration / API gateway (ASP.NET Core Web API)
  ICE.Core                     Shared domain models + service contracts
  ICE.Ingestion                Resource Ingestion Service
  ICE.KnowledgeRepresentation  Knowledge Representation Service
  ICE.Indexing                 Index Management Service
  ICE.Retrieval                Intelligent Retrieval Service
  ICE.ContextOptimization      Context Optimization Service
  ICE.ContextMemory            Context Memory Service
  ICE.Evaluation               Evaluation & Observability Service
workers/
  document-processing          Python/Docling ingestion worker (RabbitMQ consumer)
infra/
  docker-compose.yml           Postgres+pgvector, Redis, RabbitMQ, API, worker
  postgres/init.sql            Phase 1 schema (resources, knowledge_nodes, edges, metrics)
```

Each service project maps 1:1 to a row in system_architecture.md's "Core Components" table.
`ICE.Api` composes them behind three endpoint groups:

- `POST /api/resources/ingest` — register + ingest + index a resource synchronously (small/code sources).
- `POST /api/resources/{resourceId}/nodes` — accept structure extracted out-of-process by the Python worker.
- `POST /api/retrieval/ask` — progressive retrieval + context optimization for a question; returns a `ContextPackage`, not a raw document dump.
- `GET/PUT /api/memory/sessions/{sessionId}` — session knowledge memory.
- `GET /api/evaluation/recent` — recent per-task metrics.

## Run locally (no Docker)

```powershell
dotnet run --project src/ICE.Api
```

All services are currently in-memory singletons, so the API runs standalone with no external dependencies. Swagger/OpenAPI is available at `/openapi/v1.json` in development.

## Run the full stack (Docker)

```powershell
Copy-Item .env.example infra/.env
docker compose -f infra/docker-compose.yml --env-file infra/.env up --build
```

This brings up Postgres (with the `pgvector` extension and Phase 1 schema), Redis, RabbitMQ, the API, and the document-processing worker. The in-memory services are not yet wired to these — see "Next steps".

## Next steps (to move past the scaffold)

1. Replace `InMemoryIndexManagementService` with a Postgres-backed implementation (pgvector for semantic, full-text search for lexical, `relationship_edges` for graph) using the schema in `infra/postgres/init.sql`.
2. Replace `InMemoryContextMemoryService` with a Redis-backed implementation (`StackExchange.Redis` is the natural fit) so memory survives restarts and is shared across API instances.
3. Wire `ICE.Ingestion` to actually call Tree-sitter (code) and have the Python worker call Docling (documents) instead of returning a single placeholder node.
4. Implement real hybrid search (Reciprocal Rank Fusion) and reranking in `ICE.Retrieval` instead of the single lexical pass in `ProgressiveRetrievalService`.
5. Add the asynchronous publish side: `ICE.Api` should publish an ingestion job to RabbitMQ for document/website sources instead of (or in addition to) the synchronous `/ingest` path, so the worker in `workers/document-processing` has something to consume.
