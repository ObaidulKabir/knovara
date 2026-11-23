-- Schema for the Intelligent Context Engine (ICRO), Phase 1.
-- Runs once, when the Postgres volume is first created. After changing it,
-- recreate the volume: docker compose -f infra/docker-compose.yml down -v

CREATE EXTENSION IF NOT EXISTS vector;

-- One row per (tenant, URI). source_version is the content hash of the last
-- successfully extracted version; NULL until the first extraction completes.
CREATE TABLE IF NOT EXISTS resources (
    resource_id     TEXT PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    source_type     TEXT NOT NULL,       -- Code | Document | Website
    uri             TEXT NOT NULL,
    source_version  TEXT,
    status          TEXT NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending', 'processing', 'ready', 'failed')),
    last_error      TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, uri)
);

-- The hierarchy of the resource's current version. A new version replaces
-- every row for the resource in one transaction.
CREATE TABLE IF NOT EXISTS knowledge_nodes (
    node_id           TEXT PRIMARY KEY,
    resource_id       TEXT NOT NULL REFERENCES resources(resource_id) ON DELETE CASCADE,
    parent_node_id    TEXT REFERENCES knowledge_nodes(node_id) ON DELETE CASCADE,
    ordinal           INTEGER NOT NULL,   -- reading order within the resource
    level             TEXT NOT NULL,      -- document | section | paragraph | table | list_item | ...
    title             TEXT NOT NULL,      -- heading path that gives the passage its context
    summary           TEXT,
    original_content  TEXT,               -- exact source text; what the agent is finally given
    metadata          JSONB NOT NULL DEFAULT '{}',
    embedding         vector(1536),       -- Phase 2
    search_tsv        tsvector GENERATED ALWAYS AS (
                          setweight(to_tsvector('english', coalesce(title, '')), 'B') ||
                          setweight(to_tsvector('english', coalesce(original_content, '')), 'A')
                      ) STORED,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_resource ON knowledge_nodes (resource_id, ordinal);
CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_parent ON knowledge_nodes (parent_node_id);
CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_search ON knowledge_nodes USING GIN (search_tsv);

CREATE TABLE IF NOT EXISTS relationship_edges (
    from_node_id   TEXT NOT NULL REFERENCES knowledge_nodes(node_id) ON DELETE CASCADE,
    to_node_id     TEXT NOT NULL REFERENCES knowledge_nodes(node_id) ON DELETE CASCADE,
    relation_type  TEXT NOT NULL,
    PRIMARY KEY (from_node_id, to_node_id, relation_type)
);

CREATE TABLE IF NOT EXISTS task_metrics (
    task_id         TEXT PRIMARY KEY,
    input_tokens    INTEGER NOT NULL,
    latency_ms      DOUBLE PRECISION NOT NULL,
    llm_call_count  INTEGER NOT NULL,
    recall_at_k     DOUBLE PRECISION,
    answer_accurate BOOLEAN,
    citation_accurate BOOLEAN,
    cache_hit       BOOLEAN NOT NULL,
    reprocessed     BOOLEAN NOT NULL,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
