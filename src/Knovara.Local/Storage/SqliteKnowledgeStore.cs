using System.Runtime.InteropServices;
using System.Text.Json;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.Data.Sqlite;

namespace Knovara.Local.Storage;

/// <summary>
/// The Resource &amp; Knowledge Store on SQLite: the local runtime's counterpart of PostgresKnowledgeStore, behind the same
/// IKnowledgeStore contract, so retrieval, the controllers and the MCP server cannot tell the two apart. Every operation is
/// scoped by tenant, exactly as in the server store.
/// </summary>
public sealed class SqliteKnowledgeStore(SqliteDatabase db, TimeProvider? clock = null) : IKnowledgeStore
{
    private const string ResourceColumns = "resource_id, source_type, uri, source_version, tenant_id, status, last_error";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Now() => _clock.GetUtcNow().UtcDateTime.ToString("O");

    // ---------------------------------------------------------------- registering and reading resources

    public async Task<ResourceReference> RegisterResourceAsync(string tenantId, string uri, SourceType type, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await RegisterAsync(conn, null, tenantId, uri, type, ct);
    }

    private async Task<ResourceReference> RegisterAsync(SqliteConnection conn, SqliteTransaction? tx, string tenantId, string uri, SourceType type, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO resources (resource_id, tenant_id, source_type, uri, status, created_at, updated_at)
            VALUES (@id, @tenant, @type, @uri, 'pending', @now, @now)
            ON CONFLICT (tenant_id, uri) DO UPDATE
                SET status = 'pending', last_error = NULL, updated_at = @now
            RETURNING {ResourceColumns}
            """;
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("n"));
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@type", type.ToString());
        cmd.Parameters.AddWithValue("@uri", uri);
        cmd.Parameters.AddWithValue("@now", Now());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadResource(reader);
    }

    public async Task<ResourceReference?> GetResourceAsync(string tenantId, string resourceId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ResourceColumns} FROM resources WHERE resource_id = @id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("@id", resourceId);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadResource(reader) : null;
    }

    public async Task<ResourcePage> QueryResourcesAsync(string tenantId, ResourceFilter filter, CancellationToken ct = default)
    {
        var where = new List<string> { "tenant_id = @tenant" };
        if (filter.Status is not null) where.Add("status = @status");
        if (filter.Type is not null) where.Add("source_type = @type");
        var search = string.IsNullOrWhiteSpace(filter.UriContains) ? null : "%" + EscapeLike(filter.UriContains.Trim()) + "%";
        if (search is not null) where.Add("uri LIKE @uriLike ESCAPE '\\'");
        var clause = string.Join(" AND ", where);

        void Bind(SqliteCommand c)
        {
            c.Parameters.AddWithValue("@tenant", tenantId);
            if (filter.Status is { } status) c.Parameters.AddWithValue("@status", status.ToString().ToLowerInvariant());
            if (filter.Type is { } type) c.Parameters.AddWithValue("@type", type.ToString());
            if (search is not null) c.Parameters.AddWithValue("@uriLike", search);
        }

        await using var conn = await db.OpenAsync(ct);

        int total;
        await using (var count = conn.CreateCommand())
        {
            count.CommandText = $"SELECT count(*) FROM resources WHERE {clause}";
            Bind(count);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        var items = new List<ResourceReference>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ResourceColumns} FROM resources WHERE {clause} ORDER BY updated_at DESC, resource_id LIMIT @limit OFFSET @offset";
            Bind(cmd);
            cmd.Parameters.AddWithValue("@limit", filter.Limit);
            cmd.Parameters.AddWithValue("@offset", filter.Offset);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(ReadResource(reader));
        }

        return new ResourcePage(items, total);
    }

    public async Task<ResourceStats> GetStatsAsync(string tenantId, CancellationToken ct = default)
    {
        var byStatus = Enum.GetNames<ResourceStatus>().ToDictionary(n => n, _ => 0);
        var byType = Enum.GetNames<SourceType>().ToDictionary(n => n, _ => 0);

        await using var conn = await db.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT 'status', status, count(*) FROM resources WHERE tenant_id = @tenant GROUP BY status " +
                "UNION ALL SELECT 'type', source_type, count(*) FROM resources WHERE tenant_id = @tenant GROUP BY source_type";
            cmd.Parameters.AddWithValue("@tenant", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var count = (int)reader.GetInt64(2);
                if (reader.GetString(0) == "status") byStatus[Enum.Parse<ResourceStatus>(reader.GetString(1), ignoreCase: true).ToString()] = count;
                else byType[Enum.Parse<SourceType>(reader.GetString(1)).ToString()] = count;
            }
        }

        await using var totals = conn.CreateCommand();
        totals.CommandText = """
            SELECT
              (SELECT count(*) FROM knowledge_nodes n JOIN resources r ON r.resource_id = n.resource_id
                WHERE r.tenant_id = @tenant AND n.original_content IS NOT NULL),
              (SELECT count(*) FROM knowledge_nodes n JOIN resources r ON r.resource_id = n.resource_id
                WHERE r.tenant_id = @tenant AND n.embedding IS NOT NULL),
              (SELECT coalesce(sum(length(n.original_content)), 0) FROM knowledge_nodes n JOIN resources r ON r.resource_id = n.resource_id
                WHERE r.tenant_id = @tenant),
              (SELECT coalesce(sum(u.size_bytes), 0) FROM resource_uploads u JOIN resources r ON r.resource_id = u.resource_id
                WHERE r.tenant_id = @tenant)
            """;
        totals.Parameters.AddWithValue("@tenant", tenantId);
        await using var t = await totals.ExecuteReaderAsync(ct);
        await t.ReadAsync(ct);
        return new ResourceStats(byStatus.Values.Sum(), byStatus, byType, (int)t.GetInt64(0), (int)t.GetInt64(1), t.GetInt64(2), t.GetInt64(3));
    }

    public async Task<PassagePage?> ListPassagesAsync(string tenantId, string resourceId, int limit, int offset, CancellationToken ct = default)
    {
        if (await GetResourceAsync(tenantId, resourceId, ct) is null) return null;

        await using var conn = await db.OpenAsync(ct);

        int total;
        await using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM knowledge_nodes WHERE resource_id = @id AND original_content IS NOT NULL";
            count.Parameters.AddWithValue("@id", resourceId);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        var items = new List<PassageSummary>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT node_id, level, title, substr(original_content, 1, 500)
                FROM knowledge_nodes WHERE resource_id = @id AND original_content IS NOT NULL
                ORDER BY ordinal LIMIT @limit OFFSET @offset
                """;
            cmd.Parameters.AddWithValue("@id", resourceId);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new PassageSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new PassagePage(items, total);
    }

    // ---------------------------------------------------------------- deleting

    public async Task<bool> DeleteResourceAsync(string tenantId, string resourceId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resources WHERE resource_id = @id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("@id", resourceId);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<string>> DeleteResourcesAsync(
        string tenantId, IReadOnlyCollection<string>? resourceIds, ResourceStatus? status, CancellationToken ct = default)
    {
        if (resourceIds is null && status is null)
        {
            throw new ArgumentException("Bulk delete needs resource ids and/or a status; it will not delete everything by default.");
        }

        // Selectors narrow each other (AND), so ids + status means "these ids, but only if they are in this status".
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "tenant_id = @tenant" };
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        if (resourceIds is not null)
        {
            var names = resourceIds.Select((id, i) => { cmd.Parameters.AddWithValue($"@i{i}", id); return $"@i{i}"; }).ToList();
            where.Add(names.Count == 0 ? "0" : $"resource_id IN ({string.Join(",", names)})");
        }
        if (status is { } s)
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", s.ToString().ToLowerInvariant());
        }

        cmd.CommandText = $"DELETE FROM resources WHERE {string.Join(" AND ", where)} RETURNING resource_id";
        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) deleted.Add(reader.GetString(0));
        return deleted;
    }

    // ---------------------------------------------------------------- uploads

    public async Task<ResourceReference> SaveUploadAsync(string tenantId, string fileName, byte[] content, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var resource = await RegisterAsync(conn, tx, tenantId, UploadUri.For(fileName), SourceType.Document, ct);

        await using (var store = conn.CreateCommand())
        {
            store.Transaction = tx;
            store.CommandText = """
                INSERT INTO resource_uploads (resource_id, file_name, content, size_bytes, uploaded_at)
                VALUES (@id, @name, @content, @size, @now)
                ON CONFLICT (resource_id) DO UPDATE
                    SET file_name = @name, content = @content, size_bytes = @size, uploaded_at = @now
                """;
            store.Parameters.AddWithValue("@id", resource.ResourceId);
            store.Parameters.AddWithValue("@name", fileName);
            store.Parameters.AddWithValue("@content", content);
            store.Parameters.AddWithValue("@size", (long)content.Length);
            store.Parameters.AddWithValue("@now", Now());
            await store.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return resource;
    }

    public async Task<StoredUpload?> GetUploadAsync(string tenantId, string resourceId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.file_name, u.content FROM resource_uploads u
            JOIN resources r ON r.resource_id = u.resource_id
            WHERE u.resource_id = @id AND r.tenant_id = @tenant
            """;
        cmd.Parameters.AddWithValue("@id", resourceId);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new StoredUpload(reader.GetString(0), (byte[])reader.GetValue(1)) : null;
    }

    // ---------------------------------------------------------------- versions

    private const string StaleEmbeddings = """
        EXISTS (SELECT 1 FROM knowledge_nodes s
                WHERE s.resource_id = r.resource_id
                  AND s.original_content IS NOT NULL
                  AND s.embedding_model IS NOT @model)
        """;

    public async Task<VersionCheck> CheckVersionAsync(string tenantId, string resourceId, string contentHash, string embeddingModel, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE resources AS r
            SET status = CASE
                    WHEN r.source_version IS @hash AND NOT {StaleEmbeddings} THEN 'ready'
                    ELSE 'processing' END,
                last_error = NULL, updated_at = @now
            WHERE r.resource_id = @id AND r.tenant_id = @tenant
            RETURNING status
            """;
        cmd.Parameters.AddWithValue("@id", resourceId);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@model", embeddingModel);
        cmd.Parameters.AddWithValue("@now", Now());

        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => VersionCheck.NotFound,
            "ready" => VersionCheck.Unchanged,
            _ => VersionCheck.Changed
        };
    }

    public async Task<VersionCommit?> CommitVersionAsync(
        string tenantId,
        string resourceId,
        string contentHash,
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyDictionary<string, float[]> embeddings,
        string embeddingModel,
        CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // IMMEDIATE takes the write lock up front, so two commits for the same resource cannot interleave their check and write.
        await using var tx = conn.BeginTransaction(deferred: false);

        string? currentVersion;
        bool stale;
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = $"SELECT r.source_version, {StaleEmbeddings} FROM resources r WHERE r.resource_id = @id AND r.tenant_id = @tenant";
            lockCmd.Parameters.AddWithValue("@id", resourceId);
            lockCmd.Parameters.AddWithValue("@tenant", tenantId);
            lockCmd.Parameters.AddWithValue("@model", embeddingModel);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            currentVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
            stale = reader.GetInt64(1) != 0;
        }

        var applied = currentVersion != contentHash || stale;
        if (applied)
        {
            await using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM knowledge_nodes WHERE resource_id = @id";
                delete.Parameters.AddWithValue("@id", resourceId);
                await delete.ExecuteNonQueryAsync(ct);
            }

            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO knowledge_nodes
                    (node_id, resource_id, parent_node_id, ordinal, level, title, summary, original_content, metadata, embedding, embedding_model, created_at)
                VALUES (@node, @res, @parent, @ord, @level, @title, @summary, @content, @meta, @vec, @model, @now)
                """;
            var p = insert.Parameters;
            var node = p.Add("@node", SqliteType.Text); var res = p.Add("@res", SqliteType.Text); var parent = p.Add("@parent", SqliteType.Text);
            var ord = p.Add("@ord", SqliteType.Integer); var level = p.Add("@level", SqliteType.Text); var title = p.Add("@title", SqliteType.Text);
            var summary = p.Add("@summary", SqliteType.Text); var content = p.Add("@content", SqliteType.Text); var meta = p.Add("@meta", SqliteType.Text);
            var vec = p.Add("@vec", SqliteType.Blob); var model = p.Add("@model", SqliteType.Text); var now = p.Add("@now", SqliteType.Text);
            insert.Prepare();

            var stamp = Now();
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                node.Value = n.NodeId; res.Value = n.ResourceId; parent.Value = (object?)n.ParentNodeId ?? DBNull.Value;
                ord.Value = i; level.Value = n.Level; title.Value = n.Title;
                summary.Value = (object?)n.Summary ?? DBNull.Value; content.Value = (object?)n.OriginalContent ?? DBNull.Value;
                meta.Value = JsonSerializer.Serialize(n.Metadata); now.Value = stamp;
                if (embeddings.TryGetValue(n.NodeId, out var v))
                {
                    vec.Value = ToBlob(v); model.Value = embeddingModel;
                }
                else
                {
                    vec.Value = DBNull.Value; model.Value = DBNull.Value;
                }
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        ResourceReference resource;
        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = $"""
                UPDATE resources
                SET source_version = @hash, status = 'ready', last_error = NULL, updated_at = @now
                WHERE resource_id = @id
                RETURNING {ResourceColumns}
                """;
            update.Parameters.AddWithValue("@id", resourceId);
            update.Parameters.AddWithValue("@hash", contentHash);
            update.Parameters.AddWithValue("@now", Now());
            await using var reader = await update.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            resource = ReadResource(reader);
        }

        await tx.CommitAsync(ct);
        return new VersionCommit(resource, applied, applied && currentVersion != contentHash ? currentVersion : null);
    }

    public async Task<bool> MarkFailedAsync(string tenantId, string resourceId, string error, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE resources SET status = 'failed', last_error = @error, updated_at = @now WHERE resource_id = @id AND tenant_id = @tenant";
        cmd.Parameters.AddWithValue("@id", resourceId);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@now", Now());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<CurrentPassage>> GetCurrentPassagesAsync(string tenantId, IReadOnlyCollection<string> nodeIds, CancellationToken ct = default)
    {
        if (nodeIds.Count == 0) return [];

        var passages = new List<CurrentPassage>(nodeIds.Count);
        await using var conn = await db.OpenAsync(ct);
        foreach (var batch in nodeIds.Chunk(500))
        {
            await using var cmd = conn.CreateCommand();
            var names = batch.Select((id, i) => { cmd.Parameters.AddWithValue($"@n{i}", id); return $"@n{i}"; });
            cmd.CommandText = $"""
                SELECT n.node_id, n.resource_id, r.source_version, coalesce(n.original_content, n.title)
                FROM knowledge_nodes n
                JOIN resources r ON r.resource_id = n.resource_id
                WHERE r.tenant_id = @tenant AND r.source_version IS NOT NULL AND n.node_id IN ({string.Join(",", names)})
                """;
            cmd.Parameters.AddWithValue("@tenant", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                passages.Add(new CurrentPassage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        return passages;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Little-endian float32, the layout the index service reads back without copying.</summary>
    internal static byte[] ToBlob(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    /// <summary>Escapes LIKE wildcards so a search for "100%_done" matches that text literally.</summary>
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static ResourceReference ReadResource(SqliteDataReader r) => new(
        ResourceId: r.GetString(0),
        Type: Enum.Parse<SourceType>(r.GetString(1)),
        Uri: r.GetString(2),
        SourceVersion: r.IsDBNull(3) ? null : r.GetString(3),
        TenantId: r.GetString(4),
        Status: Enum.Parse<ResourceStatus>(r.GetString(5), ignoreCase: true),
        Error: r.IsDBNull(6) ? null : r.GetString(6));
}
