using Microsoft.Data.Sqlite;

namespace Knovara.Local.Storage;

/// <summary>
/// The single SQLite file that holds everything the local runtime stores: resources, the knowledge hierarchy, passage
/// embeddings (little-endian float32 blobs), an FTS5 full-text index kept in step by triggers, and uploaded originals.
/// It mirrors infra/postgres/init.sql, so the two stores behave the same behind the same contracts.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public string Path { get; }

    public SqliteDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,        // cascades from resources to passages, uploads and the full-text index depend on it
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>Creates the schema if it does not exist. Idempotent; safe to call at every start.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // WAL lets the search path read while ingestion writes; NORMAL sync is safe with WAL and much faster.
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS resources (
            resource_id     TEXT PRIMARY KEY,
            tenant_id       TEXT NOT NULL,
            source_type     TEXT NOT NULL,
            uri             TEXT NOT NULL,
            source_version  TEXT,
            status          TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'ready', 'failed')),
            last_error      TEXT,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            UNIQUE (tenant_id, uri)
        );

        CREATE TABLE IF NOT EXISTS knowledge_nodes (
            node_id           TEXT PRIMARY KEY,
            resource_id       TEXT NOT NULL REFERENCES resources(resource_id) ON DELETE CASCADE,
            parent_node_id    TEXT REFERENCES knowledge_nodes(node_id) ON DELETE CASCADE,
            ordinal           INTEGER NOT NULL,
            level             TEXT NOT NULL,
            title             TEXT NOT NULL,
            summary           TEXT,
            original_content  TEXT,
            metadata          TEXT NOT NULL DEFAULT '{}',
            embedding         BLOB,
            embedding_model   TEXT,
            created_at        TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_knowledge_nodes_resource ON knowledge_nodes (resource_id, ordinal);

        -- Full-text index over passages. External-content table + triggers is SQLite's documented way to keep it in step;
        -- the triggers also fire for rows removed by ON DELETE CASCADE, so deleting a resource leaves no stale entries.
        CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(
            title, original_content,
            content = 'knowledge_nodes', content_rowid = 'rowid',
            tokenize = 'porter unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS knowledge_nodes_ai AFTER INSERT ON knowledge_nodes BEGIN
            INSERT INTO knowledge_fts (rowid, title, original_content) VALUES (new.rowid, new.title, new.original_content);
        END;
        CREATE TRIGGER IF NOT EXISTS knowledge_nodes_ad AFTER DELETE ON knowledge_nodes BEGIN
            INSERT INTO knowledge_fts (knowledge_fts, rowid, title, original_content) VALUES ('delete', old.rowid, old.title, old.original_content);
        END;

        CREATE TABLE IF NOT EXISTS resource_uploads (
            resource_id  TEXT PRIMARY KEY REFERENCES resources(resource_id) ON DELETE CASCADE,
            file_name    TEXT NOT NULL,
            content      BLOB NOT NULL,
            size_bytes   INTEGER NOT NULL,
            uploaded_at  TEXT NOT NULL
        );
        """;
}
