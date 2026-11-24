# ICE — Intelligent Context Engine (ICRO)

Application for the Intelligent Context Engine described in
[business_architecture.md](business_architecture.md), [system_architecture.md](system_architecture.md),
and [system_specifications.md](system_specifications.md).

**Phases 1 and 2 are implemented.**

- **Phase 1, intelligent document ingestion:** PDF, DOCX, PPTX, HTML and Markdown sources are
  fetched and hashed by a Docling worker, converted into a hierarchy (document → sections →
  passages, one passage per structural unit) and stored in Postgres. An unchanged source stops
  at the version check, before any parsing.
- **Phase 2, hybrid retrieval:** passages are embedded by a self-hosted model service (open
  models on CPU, no paid APIs) and searched with Postgres full-text search, pgvector, or both,
  with a cross-encoder reranker on top. `/ask` verifies every piece of evidence against the
  current stored passage.

Context memory and evaluation are still in-memory, and section and document summaries are not
built yet.

## Layout

```
src/
  ICE.Api                      API gateway (ASP.NET Core Web API)
  ICE.Core                     Shared domain models + service contracts
  ICE.Ingestion                RabbitMQ ingestion queue
  ICE.KnowledgeRepresentation  Hierarchy validation + Postgres Resource & Knowledge Store (D1)
  ICE.Indexing                 Postgres full-text and pgvector search over stored passages
  ICE.Retrieval                Intelligent Retrieval Service (lexical, semantic, hybrid, rerank)
  ICE.Models                   Client for the model service (embeddings, reranking)
  ICE.ContextOptimization      Context Optimization Service
  ICE.ContextMemory            Context Memory Service (in-memory until Phase 3)
  ICE.Evaluation               Evaluation & Observability Service (in-memory)
workers/
  document-processing          Python/Docling extraction worker (RabbitMQ consumer)
services/
  model-service                Self-hosted embeddings and reranking (FastAPI + fastembed, CPU)
eval/
  run_benchmark.py             Retrieval benchmark over questions.json; reports in eval/results/
infra/
  docker-compose.yml           Postgres+pgvector, Redis, RabbitMQ, model service, API, worker
  postgres/                    Schema (init.sql), baked into the Postgres image
sources/
  samples/                     Sample documents for trying ingestion
```

## How ingestion works

1. `POST /api/resources/ingest` registers the resource once per (tenant, URI), marks it
   `pending`, and queues a job. It returns `202 Accepted`.
2. The worker fetches the source, computes its `sha256:` content hash, and calls
   `POST /api/resources/{id}/version`. If the hash matches the stored version, the resource
   goes back to `ready` and the worker stops there.
3. Otherwise the worker converts the source with Docling and posts the hierarchy to
   `POST /api/resources/{id}/nodes`. The API validates it, assigns its own node ids, and in one
   transaction replaces the resource's nodes and records the new version.
4. Failures are reported with `POST /api/resources/{id}/failure` and show on the resource as
   `status: Failed` with the reason.

Poll `GET /api/resources/{id}` for `status` (`Pending`, `Processing`, `Ready`, `Failed`).

Sources are either a public `http(s)` URL or a path relative to the worker's source folder.
The worker refuses URLs that resolve to private, loopback or link-local addresses; set
`ALLOW_PRIVATE_URLS=true` only for local testing.

The version hash covers the source bytes **and** the worker's `EXTRACTOR_VERSION`, because the
stored hierarchy depends on both. Bump `EXTRACTOR_VERSION` in `worker.py` whenever extraction
logic changes, and every source is reprocessed once on its next ingest.

What the hierarchy looks like:

- Markdown, HTML and DOCX keep their own heading levels; the first title becomes the document node.
- PDFs report every heading at the same level, so depth is inferred from section numbering
  ("3.1 PDF backends" goes under "3 Processing pipeline"), unnumbered headings go under the most
  recent numbered one, and a heading that opens page 1 becomes the document title.
- Each passage's title is its heading path (`Document > Section > Subsection`), and it is indexed
  with the passage, so a search for a section's topic finds its passages.
- Table and figure captions are kept inside their table or figure, not repeated as separate passages.
- Each structural unit (paragraph, list, table, figure, code block) becomes a passage. Sizes are
  counted in the embedding model's tokens, because the embedder and reranker only read 512.
- Units under `MIN_CHUNK_TOKENS` (default 64), such as an author line or a one-line paragraph,
  are joined with their neighbours in the same section, and the items of one list stay together.
  Joining never crosses a heading, and tables, figures and code blocks are never joined.
- Passages over `MAX_CHUNK_TOKENS` (default 384) are split on sentence boundaries; tables are
  split between rows with the header row repeated in every piece.

Worker settings (environment variables): `MIN_CHUNK_TOKENS`, `MAX_CHUNK_TOKENS`, `MAX_SOURCE_BYTES` (50 MB),
`DOCLING_DO_OCR` (`false`; turn on for scanned PDFs, at a large cost in time), `ALLOW_PRIVATE_URLS`.

PDFs are the slow case: the 9-page Docling technical report took 2–5 minutes on this machine's
CPU, while Markdown, HTML and DOCX take well under a second.

## How retrieval works

When the API stores a version's passages, it has the model service embed each one as its heading
path plus its text, and keeps the vector in `knowledge_nodes.embedding` along with the model's
name. A resource whose stored embeddings come from a different model counts as changed on its
next ingest, so switching models re-embeds it.

There are four retrieval modes. `/ask` uses `HybridRerank`; `POST /api/retrieval/search` runs
any one of them so they can be compared.

| Mode | What it does |
|---|---|
| `Lexical` | Postgres full-text search. Matches **any** of the question's terms and ranks passages by how many match and how close together. |
| `Semantic` | Embedding similarity through pgvector (HNSW, cosine distance), filtered by tenant and embedding model. |
| `Hybrid` | The top 30 lexical and top 30 semantic results merged with Reciprocal Rank Fusion. |
| `HybridRerank` | The merged candidates (typically 30–40) reordered by a cross-encoder, which reads question and passage together. |

On the sample corpus, `Semantic` and `HybridRerank` both put a correct passage in the top 3 for
90% of the benchmark questions, against 68% for `Lexical`, which mostly misses questions worded
differently from the document. `Semantic` answers in about 30 ms; `HybridRerank` takes about
2.5 s on CPU for a slightly better first result. See [eval/results](eval/results) and
system_architecture.md "Retrieval Strategy by Data Size" for how this should change as data grows.

### Model service

`services/model-service` runs `BAAI/bge-small-en-v1.5` (384-dimensional embeddings) and
`Xenova/ms-marco-MiniLM-L-6-v2` (reranker) through ONNX Runtime on CPU. Both models are baked
into its image. Changing the embedding model means rebuilding the image, matching the
`vector(384)` column in `init.sql` and the worker's `CHUNK_TOKENIZER_MODEL` build argument, and
re-ingesting.

Settings: `MODEL_THREADS` (2 in docker-compose) and `INFERENCE_BATCH_SIZE` (8). Inference runs in
small length-sorted batches because one large batch of long passages exhausted the container's
memory; a full benchmark run now peaks around 600 MB of its `MODEL_SERVICE_MEM_LIMIT` (2200m).

### Benchmark

```powershell
python eval/run_benchmark.py
```

It asks the 40 questions in `eval/questions.json` in every mode, against the API on
`127.0.0.1:5299` for tenant `acme`, and writes a Markdown and JSON report to `eval/results/`:
Recall@1/3/5/10, MRR, Recall@3 for questions that use the document's wording and for paraphrased
ones, the size of the top 3 passages, and p50/p95 latency. A passage counts as correct if it
contains one of the question's expected fragments. The questions cover the three files in
`sources/samples` and the Docling technical report (https://arxiv.org/pdf/2408.09869), all
ingested for tenant `acme`.

## Endpoints

Every request carries an `X-Tenant-Id` header (see `TenantHeader.cs`); all reads are filtered by it.

- `POST /api/resources/ingest` — `{ "uri": "samples/payment-service-guide.md", "type": "Document" }`. `type` is `Document` or `Website`; `Code` returns 400 until Phase 4.
- `GET /api/resources/{resourceId}` — the resource, with its current version and status.
- `POST /api/resources/{resourceId}/version`, `/nodes`, `/failure` — called by the worker.
- `POST /api/retrieval/ask` — `{ "sessionId": "s1", "question": "maximum retry interval" }`. Evidence cache → on a miss, `HybridRerank` retrieval → each passage re-read from Postgres and dropped if its source has changed → context optimization. Returns a `ContextPackage`.
- `POST /api/retrieval/search` — `{ "question": "maximum retry interval", "mode": "Semantic", "topK": 10 }`. Runs one retrieval mode with no cache and no context optimization, for the benchmark. `mode` is `Lexical`, `Semantic`, `Hybrid` or `HybridRerank` (default); `topK` defaults to 5.
- `GET/PUT /api/memory/sessions/{sessionId}` — session knowledge memory.
- `GET /api/evaluation/recent` — recent per-task metrics, including whether the evidence cache was hit.

## Run locally

Start Postgres, RabbitMQ and the model service, then run the API on the host:

```powershell
docker compose -f infra/docker-compose.yml up -d --build postgres rabbitmq model-service
```

```powershell
dotnet run --project src/ICE.Api --urls http://0.0.0.0:5299
```

`appsettings.Development.json` points the API at Postgres on port 5442, RabbitMQ on 5672 and the
model service on 8090.
Then start the worker, pointed at the API on the host:

```powershell
$env:ICE_API_BASE_URL = "http://host.docker.internal:5299"; docker compose -f infra/docker-compose.yml up -d --build document-processing-worker
```

The first worker build takes several minutes: it installs CPU-only PyTorch and Docling and
downloads Docling's layout and table models into the image.

### Adding source files

The worker reads local sources from the `ice_sources` Docker volume (mounted at `/sources`),
which avoids depending on Docker Desktop's host file sharing. Copy files into it with:

```powershell
tar -C sources -cf - . | docker run --rm -i -v ice_sources:/sources alpine tar -xf - -C /sources
```

Then ingest them by their path inside that folder, e.g. `samples/payment-service-guide.md`.

## Run the full stack (Docker)

```powershell
Copy-Item .env.example infra/.env
docker compose -f infra/docker-compose.yml --env-file infra/.env up --build
```

The API is then on http://localhost:5000. Host ports are set by `POSTGRES_HOST_PORT` (5442),
`RABBITMQ_HOST_PORT` (5672), `REDIS_HOST_PORT` (6379), `MODEL_SERVICE_HOST_PORT` (8090) and
`API_HOST_PORT` (5000).

Memory: the model service and worker are capped by `MODEL_SERVICE_MEM_LIMIT` (2200m) and
`WORKER_MEM_LIMIT` (1800m) and restart if they exceed it. Give Docker at least 4 GB; with the
2 GB default, converting the sample PDF ran out of memory.

### Schema changes

`infra/postgres/init.sql` runs only when the Postgres volume is first created. After changing
it, rebuild and recreate the volume (this deletes stored data):

```powershell
docker compose -f infra/docker-compose.yml down -v; docker compose -f infra/docker-compose.yml up -d --build postgres
```

## Next steps

0. **Authentication.** The tenant currently comes from a header the caller sets, which is only safe behind a trusted gateway. The worker-facing endpoints (`version`, `nodes`, `failure`) should also require a worker credential. Add authentication middleware that derives the tenant from the verified credential and sets it where controllers read `TenantHeader`.
1. **Phase 2 remainder:** section and document summaries (with a self-hosted LLM), and tier S1 of system_architecture.md "Retrieval Strategy by Data Size": rerank only the top 20 semantic and top 10 keyword results (about 1 s instead of 2.5 s), rerank only when semantic results look uncertain, a 200+ question benchmark and the distractor scaling test.
2. **Phase 3 — context memory:** replace `InMemoryContextMemoryService` with Redis, and persist evaluation metrics to the `task_metrics` table.
3. **Phase 4 — code intelligence:** Tree-sitter parsing for `Code` sources.
4. Harden the worker's URL fetching against DNS rebinding (resolve once and connect to the checked address).
5. The PDF heading heuristic nests unnumbered back-matter ("References", "Appendix") under the last numbered section; a font-size or position signal from Docling would fix that.
