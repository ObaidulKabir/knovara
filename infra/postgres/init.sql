-- Initial schema for the Intelligent Context Engine (ICRO).
-- Phase 1 scope: metadata + hierarchy + full-text + vector, all in Postgres,
-- per system_architecture.md "Recommended Technology Stack".

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS resources (
    resource_id     TEXT PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    source_type     TEXT NOT NULL,       -- Code | Document | Website
    uri             TEXT NOT NULL,
    source_version  TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS knowledge_nodes (
    node_id           TEXT PRIMARY KEY,
    resource_id       TEXT NOT NULL REFERENCES resources(resource_id) ON DELETE CASCADE,
    parent_node_id    TEXT REFERENCES knowledge_nodes(node_id) ON DELETE CASCADE,
    level             TEXT NOT NULL,      -- document | chapter | section | topic | passage
    title             TEXT NOT NULL,
    summary           TEXT,
    original_content  TEXT,
    metadata          JSONB NOT NULL DEFAULT '{}',
    embedding         vector(1536),       -- adjust to the chosen embedding model's dimension
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_resource ON knowledge_nodes (resource_id);
CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_parent ON knowledge_nodes (parent_node_id);
CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_fts ON knowledge_nodes
    USING GIN (to_tsvector('english', coalesce(title, '') || ' ' || coalesce(summary, '') || ' ' || coalesce(original_content, '')));

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
