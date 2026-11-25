# Knovara

Application for **Knovara** (knovara.net), the context retrieval and optimization engine described in
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
  Knovara.Api                      API gateway (ASP.NET Core Web API)
  Knovara.Cli                      Thin CLI over the API (no project reference to the server)
  Knovara.Mcp                      MCP server (stdio) exposing the API as tools to Claude and other MCP clients
  Knovara.ControlPlane             SaaS control plane: accounts, tenants, desktop enrollment, installer downloads
  Knovara.Core                     Shared domain models + service contracts
  Knovara.Ingestion                RabbitMQ ingestion queue
  Knovara.KnowledgeRepresentation  Hierarchy validation + Postgres Resource & Knowledge Store (D1)
  Knovara.Indexing                 Postgres full-text and pgvector search over stored passages
  Knovara.Retrieval                Intelligent Retrieval Service (lexical, semantic, hybrid, rerank)
  Knovara.Models                   Client for the model service (embeddings, reranking)
  Knovara.ContextOptimization      Context Optimization Service
  Knovara.ContextMemory            Context Memory Service (in-memory until Phase 3)
  Knovara.Evaluation               Evaluation & Observability Service (in-memory)
workers/
  document-processing          Python/Docling extraction worker (RabbitMQ consumer)
services/
  model-service                Self-hosted embeddings and reranking (FastAPI + fastembed, CPU)
eval/
  run_benchmark.py             Retrieval benchmark over questions.json; reports in eval/results/
installer/
  build-installer.ps1          Builds the Windows desktop installer (Inno Setup)
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

There are five retrieval modes. `/ask` uses `Adaptive`; `POST /api/retrieval/search` runs any
one of them so they can be compared.

| Mode | What it does |
|---|---|
| `Lexical` | Postgres full-text search. Matches **any** of the question's terms and ranks passages by how many match and how close together. |
| `Semantic` | Embedding similarity through pgvector (HNSW, cosine distance), filtered by tenant and embedding model. |
| `Hybrid` | The top 30 lexical and top 30 semantic results merged with Reciprocal Rank Fusion. |
| `HybridRerank` | The top 20 semantic and top 10 lexical results (up to 30 after removing duplicates) reordered by a cross-encoder, which reads question and passage together. |
| `Adaptive` | Runs `Semantic` first. Only when its top two results score within `AdaptiveMarginThreshold` (default 0.038) does it add the lexical candidates and rerank, reusing the semantic results it already has. |

On a 215-question benchmark over 7 documents (3 samples plus 4 arXiv papers, 290 passages),
`HybridRerank` reaches R@3 0.80 and MRR 0.76, against R@3 0.67 for `Semantic` alone and R@3 0.54
for `Lexical`, which mostly misses questions worded differently from the document. `Semantic`
answers in about 34 ms; `HybridRerank` takes about 1.8 s on CPU for a markedly better result.
`Adaptive` matches `HybridRerank`'s accuracy within noise (R@3 0.79, MRR 0.75) while reranking
only 77% of questions — on this corpus, most questions turn out to need it, so `Adaptive` saves
latency on roughly one question in four rather than most of them. The margin threshold was fit
on this question set (see "Choosing the margin threshold" below); a corpus with more
unambiguous, well-separated questions would escalate less often. See [eval/results](eval/results)
and system_architecture.md "Retrieval Strategy by Data Size" for how this should change as data
grows.

### Choosing the margin threshold

`eval/questions.json`'s 215 questions were split into two halves (alternating, so every document
appears in both). For each half, the threshold was swept over candidate values and the smallest
one was kept whose R@3 and MRR stayed within 0.01 of always reranking, measured on *that* half;
it was then scored on the *other* half. Both halves picked thresholds within 0.004 of each other
(0.038 and 0.034), so 0.038 — the value fit on the full set — was kept as the shipped default.
Lower thresholds escalate less often but cost more accuracy; see the margin-rule row in
`eval/results` for the full sweep.

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

It asks the 215 questions in `eval/questions.json` in every mode, against the API on
`127.0.0.1:5299` for tenant `acme`, and writes a Markdown and JSON report to `eval/results/`:
Recall@1/3/5/10, MRR, Recall@3 for questions that use the document's wording and for paraphrased
ones, the size of the top 3 passages, the share of answers the API reranked, and p50/p95 latency.
A passage counts as correct if it contains one of the question's expected fragments. The
questions cover the three files in `sources/samples` and four arXiv papers — the Docling
technical report (2408.09869), Sentence-BERT (1908.10084), Dense Passage Retrieval (2004.04906)
and ColBERT (2004.12832) — all ingested for tenant `acme`.

## Endpoints

Interactive docs (Development only, same as `/openapi/v1.json` itself — not exposed by default in
a non-Development environment): run the API as in "Run locally" below, then open
http://localhost:5299/swagger. It renders the one spec `MapOpenApi` already serves, so the two can
never drift apart. Every enum (`SourceType`, `RetrievalMode`, ...) is rewritten to the string values
the API actually sends and expects (`"Document"`, `"Adaptive"`, ...) — the generator's own default
would show them as integers with no listed values, which `"Try it out"` would get wrong.

Every request carries an `X-Tenant-Id` header (see `TenantHeader.cs`); all reads are filtered by it.
How far that header is trusted depends on the "Authentication" setting below.

- `POST /api/resources/ingest` — `{ "uri": "samples/payment-service-guide.md", "type": "Document" }`. `type` is `Document` or `Website`; `Code` returns 400 until Phase 4.
- `GET /api/resources/{resourceId}` — the resource, with its current version, status and, if it `Failed`, the `error` reason.
- `GET /api/resources` — the caller's resources, most recently updated first. Filter with `?status=Failed`, `?type=Website` and `?q=` (case-insensitive substring of the URI); page with `?limit=` (1-500, default 100) and `?offset=`. The total number of matches is in the `X-Total-Count` response header, so the body stays a plain array. Backs the desktop app's "Documents" tab.
- `GET /api/resources/stats` — what the caller has stored: resources by status and type, searchable passages, embedded passages, characters of extracted text, bytes of uploaded originals. Use it to decide whether to prune.
- `GET /api/resources/{resourceId}/passages?limit=&offset=` — the passages actually extracted and stored for the resource (text truncated to 500 characters), in reading order; to check a document was understood as expected.
- `POST /api/resources/upload` — add a document from content you hold, with no shared volume or public URL: `{ "fileName": "runbook.md", "content": "# text…" }` or `{ "fileName": "report.pdf", "contentBase64": "…" }`. Supports pdf, docx, pptx, html, md (a `.txt` name is stored as `.md`); up to 25 MB. Returns `202` and the resource, like ingest. Uploading the same `fileName` again **replaces** the document and re-processes it. The bytes are kept in Postgres (`resource_uploads`) and the worker fetches them back from the API, so the worker needs `KNOVARA_API_BASE_URL` to reach it (it already does for its callbacks).
- `GET /api/resources/{resourceId}/content` — the original bytes of an uploaded document (`404` for URL-sourced resources).
- `POST /api/resources/{resourceId}/reingest` — queue an existing resource for processing again (retry a `Failed` one, or pick up a changed web page). Its current content stays searchable until the new version is ready.
- `DELETE /api/resources/{resourceId}` — deletes the resource and everything derived from it (passages, embeddings, relationships, the uploaded original — all cascade from the one row in Postgres). `204` on success, `404` if it didn't exist for this tenant. Does not cancel a job already queued for it.
- `POST /api/resources/delete` — bulk delete: `{ "resourceIds": ["…"] }` and/or `{ "status": "Failed" }` (when both are given a resource must match both). Returns `{ "deleted": [...], "notDeleted": [...] }`; ids that are missing, belong to another tenant, or are in a different status land in `notDeleted`. One selector is required (max 1000 ids): there is deliberately no "delete everything" call, so wiping a tenant means listing and naming the ids.
- `POST /api/resources/{resourceId}/version`, `/nodes`, `/failure` — called by the worker.
- `POST /api/retrieval/ask` — `{ "sessionId": "s1", "question": "maximum retry interval" }`. Evidence cache → on a miss, `Adaptive` retrieval → each passage re-read from Postgres and dropped if its source has changed → context optimization. Returns a `ContextPackage`. An optional `"documents": [{ "title": "...", "content": "...", "source": "..." }]` attaches ad-hoc text (the desktop app's "Attach Files" and "Attach Folder" use this, as does dragging files or folders onto the panel - results show inline with an Undo, each document lists its name, folder and estimated tokens with a one-click remove, and the summary warns when the attachments exceed the Ask token budget; a folder attaches every text file under it, recursively, up to 200 files / 1 MB each / 8 MB total, skipping `node_modules`, `bin`, `obj`, `.git` and similar, and reports what it skipped. Local to the desktop app: nothing is ingested into the corpus). Each document is chunked on structural boundaries ([`PinnedDocumentChunker`](src/Knovara.ContextOptimization/PinnedDocumentChunker.cs), 300-token chunks) and every chunk is scored against the question by the same cross-encoder `HybridRerank`/`Adaptive` use — it is genuinely retrieved, not pinned in whole regardless of relevance. A document large enough to need more than one chunk only contributes the chunks that actually answer the question once the token budget is tight; attached content still ranks above ordinary corpus evidence (`PinnedDocumentBaseScore`), since the caller chose to attach it. If the model service is unreachable, chunks fall back to their original order rather than failing the request. All chunks of all attached documents are scored together in batched model-service calls, so a folder costs about the same per chunk as a single file.
- `POST /api/retrieval/search` — `{ "question": "maximum retry interval", "mode": "Semantic", "topK": 10 }`. Runs one retrieval mode with no cache and no context optimization, for the benchmark. `mode` is `Lexical`, `Semantic`, `Hybrid`, `HybridRerank` or `Adaptive` (default); `topK` defaults to 5. The response's `reranked` field says whether that answer went through the cross-encoder.
- `GET/PUT /api/memory/sessions/{sessionId}` — session knowledge memory.
- `GET /api/evaluation/recent` — recent per-task metrics, including whether the evidence cache was hit.

## Authentication

Off by default. `TenantAuthMiddleware` then passes the `X-Tenant-Id` header through untouched, which
is only safe on a developer machine or behind a gateway that sets the header itself — any caller can
otherwise name any tenant.

Turn it on in configuration (or with `Authentication__Enabled=true` / `Authentication__SigningKey=…`
in the environment):

```json
"Authentication": {
  "Enabled": true,
  "SigningKey": "<at least 32 bytes, from a secret store — never commit one>",
  "TenantClaim": "tid"
}
```

With it on, every route — including the worker-facing ones — requires a valid bearer token, and the
tenant comes only from the token's `TenantClaim`, **overwriting** whatever header the caller sent, so
a spoofed header cannot reach another tenant. A request with no token is `401`; a valid token with no
tenant claim is `403`. `Issuer` and `Audience` are validated only when set. A key shorter than 32
bytes fails at startup rather than on the first request, because HS256 would reject it at validation
time. JWT inbound claim mapping is disabled, so `TenantClaim` matches the token's own name rather than
being silently rewritten to a legacy URI.

Still missing before this is real authentication: nothing issues tokens yet, and the Docling worker
has no credential of its own — so enabling it today also locks out the worker, the CLI and the
benchmark. The middleware is unit-tested, but the end-to-end token path was verified by hand; an
integration test through the real JWT pipeline needs `Microsoft.AspNetCore.Mvc.Testing`.

## CLI

`Knovara.Cli` is a thin usability shell over those endpoints — the kind of demonstration/dogfooding
layer system_architecture.md's "Application Type & Deployment Model" calls for alongside the core
engine, not a design investment. It has no project reference to the server: it speaks the same
public HTTP contract any external client would.

```powershell
dotnet run --project src/Knovara.Cli -- ingest samples/payment-service-guide.md --tenant acme
dotnet run --project src/Knovara.Cli -- watch  <resourceId> --tenant acme
dotnet run --project src/Knovara.Cli -- ask    "how long are payment logs retained" --tenant acme
dotnet run --project src/Knovara.Cli -- search "maximum retry interval" --mode Semantic --tenant acme
```

`--api` (default `http://127.0.0.1:5299`, or `$KNOVARA_API_URL`) and `--tenant` (required: pass it
or set `$KNOVARA_TENANT`) work on every command. Run `dotnet run --project src/Knovara.Cli -- help`
for the full list. `status`/`watch` show a failed resource's reason.

## MCP server

`Knovara.Mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server (stdio, built on the
official open-source C# SDK), so Claude Code, Claude Desktop and any other MCP client can ground answers
in a tenant's knowledge base. Like the CLI it has no project reference to the server: it calls the public
HTTP API.

| Tool | What it does | Annotations |
|---|---|---|
| `knovara_ask` | Source-cited passages for a question, packed to a token budget | read-only |
| `knovara_search` | One retrieval mode (`Adaptive`, `Lexical`, `Semantic`, `Hybrid`, `HybridRerank`) with raw ranked passages | read-only |
| `knovara_list_documents` | Documents with status and failure reasons; filter by status/type/text, page with limit/offset | read-only |
| `knovara_get_stats` | Size of the volume: documents by status/type, passages, embeddings, text and upload bytes | read-only |
| `knovara_get_document_status` | Status (and failure reason) of one document | read-only |
| `knovara_list_passages` | The passages actually stored for a document, to check it was understood | read-only |
| `knovara_upload_document` | Add a text/markdown document from content you provide; the same fileName replaces it | writes |
| `knovara_ingest` | Queue a URL / worker-volume path for ingestion (async; poll the status tool) | writes |
| `knovara_reingest_document` | Process an existing document again (retry a failure, refresh a page) | writes |
| `knovara_delete_document` | Permanently delete one document and its derived data | **destructive** |
| `knovara_delete_documents` | Permanently delete many, by ids and/or status (e.g. every `Failed`); never "everything" | **destructive** |

Configuration is by environment variable:

| Variable | |
|---|---|
| `KNOVARA_TENANT_ID` | **Required.** The one tenant this server can touch. |
| `KNOVARA_API_URL` | Default `http://127.0.0.1:5299`. |
| `KNOVARA_JWT` | Bearer token, when the API runs with `Authentication:Enabled`. |

The tenant is deliberately server configuration and not a tool argument, so a model (or text injected into a
document it read) cannot switch to another tenant's data. The server exits with an error if it is not set.

Build once, then register it with your client (use the absolute path to the built exe):

```powershell
dotnet build src/Knovara.Mcp
claude mcp add knovara --env KNOVARA_TENANT_ID=acme -- "C:\path\to\src\Knovara.Mcp\bin\Debug\net10.0\knovara-mcp.exe"
```

or in a client's JSON config (`.mcp.json`, Claude Desktop's `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "knovara": {
      "command": "C:\\path\\to\\src\\Knovara.Mcp\\bin\\Debug\\net10.0\\knovara-mcp.exe",
      "env": { "KNOVARA_TENANT_ID": "acme", "KNOVARA_API_URL": "http://127.0.0.1:5299" }
    }
  }
}
```

The API must be running (see "Run locally"). `knovara_ingest` takes URLs or files in the worker's source
volume, not paths on the client machine; to add content you hold, use `knovara_upload_document` (text/markdown;
the REST upload endpoint also takes pdf, docx, pptx and html). Over stdio the server only
speaks to the local client that launched it; it opens no network port.

## SaaS control plane

`Knovara.ControlPlane` is the SaaS side of the hybrid, desktop-first design: it decides **who may use Knovara and
from which machines**, while the retrieval engine (`Knovara.Api`) and the desktop app do the work. It is a
self-hosted ASP.NET Core app with a server-rendered dashboard, using the same Postgres (its tables are all `cp_*`
and are created at startup). The retrieval API never depends on it: a customer's knowledge is not touched when
a tenant is removed from the control plane.

```
 operator / tenant admin ──▶ dashboard ── creates tenants, enrollment codes; revokes devices; publishes installers
                                │
 desktop app ──(code)──▶ POST /api/enroll ──▶ device id + secret   (secret stored DPAPI-protected on the PC)
 desktop app ──(secret)─▶ POST /api/device/token ──▶ 15-minute JWT carrying the tenant
 desktop app ──(JWT)───▶ Knovara.Api    (validates the JWT; the tenant comes only from the token)
```

**Roles.** A *platform* account (the operator of this deployment) manages its tenants, accounts and installer releases. A
*tenant* account administers only its own tenant's devices and enrollment codes; every dashboard action is scoped by the
signed-in account, never by a tenant id in the request, so a tampered link or form cannot reach another tenant.
Sign-in uses local accounts (salted PBKDF2 hashes, 12+ character passwords, lockout for 15 minutes after 5 failures).

**Enrollment.** An admin creates a one-time (or limited-use) code on the Enrollment codes page: tenant, validity
(1-30 days), uses. The code (`KNV-XXXX-XXXX-XXXX`, ~59 bits) is shown once and stored only as a hash. In the
desktop app, *Enroll with Knovara* takes the control plane address, the code and a name for the PC. A tenant can
have a device limit; the Devices page lists every PC with its app version and last check-in and can revoke or
restore it.

**Revocation is not instant.** The API validates tokens statelessly, so a revoked device keeps working until
its current token expires, at most `Tokens:LifetimeMinutes` (default 15). It is refused a new token immediately.

### Run it

```powershell
# one key shared by the control plane (signs) and the API (validates); keep it secret, 32+ bytes
$key = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])

# control plane (Postgres must be running; see "Run locally")
$env:Tokens__SigningKey = $key
$env:DataPlane__BaseUrl = "http://localhost:5299"          # where enrolled desktops are told to send requests
$env:Bootstrap__AdminEmail = "you@example.com"             # first platform account; only used when none exist
$env:Bootstrap__AdminPassword = "<12+ characters>"
dotnet run --project src/Knovara.ControlPlane --urls http://localhost:5400

# the API must validate those tokens: same key, issuer and audience
$env:Authentication__Enabled = "true"
$env:Authentication__SigningKey = $key
$env:Authentication__Issuer = "knovara-control-plane"
$env:Authentication__Audience = "knovara-api"
dotnet run --project src/Knovara.Api
```

Open `http://localhost:5400`, sign in, create a tenant, then create an enrollment code for it. With Docker:
`docker compose -f infra/docker-compose.yml --profile saas up -d control-plane` (set `TOKEN_SIGNING_KEY`,
`CONTROL_PLANE_ADMIN_EMAIL`, `CONTROL_PLANE_ADMIN_PASSWORD`). Remove the bootstrap settings after the first start.
`Tokens:SigningKey` is required and checked at startup; the dashboard cookie is `Secure` whenever the request is
HTTPS, so **serve it over HTTPS (a reverse proxy is enough) before exposing it**.

Desktop-facing endpoints (JSON, no cookie): `POST /api/enroll` `{ code, deviceName, appVersion, os }`,
`POST /api/device/token` `{ deviceId, deviceSecret, appVersion, os }` (both return the API address, a token and the
newest published installer version), `GET /api/releases/latest` (public version metadata). Enrollment is rate limited
per IP and answers an unknown, expired, revoked or used-up code identically.

### Desktop installer

The dashboard's **Downloads** page lists published installers with their SHA-256; any signed-in user can download
one, and the operator uploads new versions (`.exe`, `.msi` or `.zip`) and chooses which is current. Build one with:

```powershell
.\installer\build-installer.ps1 -Version 1.1.0 -ControlPlaneUrl https://control.example.com
```

That publishes the app self-contained (users do not need .NET), then compiles `installer/knovara.iss` with
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (free) into `artifacts/installer/Knovara-Desktop-Setup-1.1.0.exe`.
It installs per user (no administrator rights), adds a Start Menu entry and uninstaller, and pre-fills the
enrollment screen with `-ControlPlaneUrl` so users only need their code. Uninstalling keeps the enrollment, so
upgrades do not make anyone enroll again. A running app shows *Update available* when the control plane has a newer
current version than the one installed.

**The installer is not code-signed**, so Windows SmartScreen warns on first run until you sign it with a certificate
you buy; the dashboard publishes checksums so downloads can at least be verified.

### Direction

This control plane was built before the local runtime, as a multi-tenant service. The architecture decision recorded in
`system_architecture.md` ("ADR-001") reshapes it as one customer's authority domain per instance (run by the customer or hosted
for them), with enrollment optional: Knovara itself is meant to run fully locally without any account. Treat the multi-tenant
operator model described here as the current behaviour, not the destination.

Licensing direction (decided, see `business_architecture.md`): this authority-domain code is part of the open core and is released under Apache-2.0 (see `LICENSE`).
SSO/SCIM, audit retention, the policy engine, federation management and the multi-customer operator layer will be separate, proprietary modules that attach to it;
none of them is in this repository.

### Known limits

- **Turning on API authentication currently breaks ingestion.** The document worker calls the API without a
  credential, so with `Authentication:Enabled` its callbacks get `401`. Retrieval and management from enrolled
  desktops work; ingesting does not until the worker gets a service credential.
- **The MCP server and CLI have no credential of their own.** With authentication on they need `KNOVARA_JWT`, and a
  device token only lasts 15 minutes. They need service tokens (or API keys) issued from the dashboard.
- Tokens are signed with one shared symmetric key (HS256) known to both services; fine for one deployment, replace
  it with an asymmetric key pair before several parties validate tokens.
- No password-reset email: a forgotten password needs the operator (or a database edit). There is no tenant
  deletion or self-service sign-up on purpose.

## Run locally

Start Postgres, RabbitMQ and the model service, then run the API on the host:

```powershell
docker compose -f infra/docker-compose.yml up -d --build postgres rabbitmq model-service
```

```powershell
dotnet run --project src/Knovara.Api --urls http://0.0.0.0:5299
```

`appsettings.Development.json` points the API at Postgres on port 5442, RabbitMQ on 5672 and the
model service on 8090.
Then start the worker, pointed at the API on the host:

```powershell
$env:KNOVARA_API_BASE_URL = "http://host.docker.internal:5299"; docker compose -f infra/docker-compose.yml up -d --build document-processing-worker
```

The first worker build takes several minutes: it installs CPU-only PyTorch and Docling and
downloads Docling's layout and table models into the image.

### Adding source files

The worker reads local sources from the `knovara_sources` Docker volume (mounted at `/sources`),
which avoids depending on Docker Desktop's host file sharing. Copy files into it with:

```powershell
tar -C sources -cf - . | docker run --rm -i -v knovara_sources:/sources alpine tar -xf - -C /sources
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

## License

Knovara is licensed under the [Apache License 2.0](LICENSE); see [`NOTICE`](NOTICE) for attribution. Contributions are accepted under the Developer Certificate of Origin
(sign off your commits with `git commit -s`); see [`CONTRIBUTING.md`](CONTRIBUTING.md). The licence does not cover the Knovara name or logo. What is open and what is a paid, proprietary
module is set out in [`business_model_and_monetization_policy.md`](business_model_and_monetization_policy.md).

## Next steps

0. **Authentication.** The middleware and token validation exist (see "Authentication" above) but are off by default, because nothing issues tokens yet and the worker has no credential of its own. Finish it: a token endpoint or external issuer, a worker credential for `version`/`nodes`/`failure`, and an integration test through the real JWT pipeline.
1. **Phase 2 remainder:** section and document summaries (with a self-hosted LLM), and tier S1 of system_architecture.md "Retrieval Strategy by Data Size": the distractor scaling test, and revisiting `AdaptiveMarginThreshold` once the question set covers corpora where most questions are *not* ambiguous (this one escalates 77% of the time, so the latency saving is smaller than hoped).
2. **Phase 3 — context memory:** replace `InMemoryContextMemoryService` with Redis, and persist evaluation metrics to the `task_metrics` table.
3. **Phase 4 — code intelligence:** Tree-sitter parsing for `Code` sources.
4. Harden the worker's URL fetching against DNS rebinding (resolve once and connect to the checked address).
5. The PDF heading heuristic nests unnumbered back-matter ("References", "Appendix") under the last numbered section; a font-size or position signal from Docling would fix that.
