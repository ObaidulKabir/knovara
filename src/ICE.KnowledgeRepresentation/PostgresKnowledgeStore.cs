using System.Text.Json;
using ICE.Core.Contracts;
using ICE.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace ICE.KnowledgeRepresentation;

/// <summary>Postgres implementation of the Resource &amp; Knowledge Store (D1). Schema: infra/postgres/init.sql.</summary>
public sealed class PostgresKnowledgeStore(NpgsqlDataSource dataSource) : IKnowledgeStore
{
    private const string ResourceColumns = "resource_id, source_type, uri, source_version, tenant_id, status";

    public async Task<ResourceReference> RegisterResourceAsync(string tenantId, string uri, SourceType type, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            INSERT INTO resources (resource_id, tenant_id, source_type, uri, status)
            VALUES (@id, @tenant, @type, @uri, 'pending')
            ON CONFLICT (tenant_id, uri) DO UPDATE
                SET status = 'pending', last_error = NULL, updated_at = now()
            RETURNING {ResourceColumns}
            """);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("n"));
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("type", type.ToString());
        cmd.Parameters.AddWithValue("uri", uri);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadResource(reader);
    }

    public async Task<ResourceReference?> GetResourceAsync(string tenantId, string resourceId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {ResourceColumns} FROM resources WHERE resource_id = @id AND tenant_id = @tenant");
        cmd.Parameters.AddWithValue("id", resourceId);
        cmd.Parameters.AddWithValue("tenant", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadResource(reader) : null;
    }

    public async Task<VersionCheck> CheckVersionAsync(string tenantId, string resourceId, string contentHash, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE resources
            SET status = CASE WHEN source_version IS NOT DISTINCT FROM @hash THEN 'ready' ELSE 'processing' END,
                last_error = NULL, updated_at = now()
            WHERE resource_id = @id AND tenant_id = @tenant
            RETURNING status
            """);
        cmd.Parameters.AddWithValue("id", resourceId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("hash", contentHash);

        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => VersionCheck.NotFound,
            "ready" => VersionCheck.Unchanged,
            _ => VersionCheck.Changed
        };
    }

    public async Task<VersionCommit?> CommitVersionAsync(string tenantId, string resourceId, string contentHash, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string? currentVersion;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT source_version FROM resources WHERE resource_id = @id AND tenant_id = @tenant FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("id", resourceId);
            lockCmd.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            currentVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        var applied = currentVersion != contentHash;
        if (applied)
        {
            await using (var delete = new NpgsqlCommand("DELETE FROM knowledge_nodes WHERE resource_id = @id", conn, tx))
            {
                delete.Parameters.AddWithValue("id", resourceId);
                await delete.ExecuteNonQueryAsync(ct);
            }

            await CopyNodesAsync(conn, nodes, ct);
        }

        ResourceReference resource;
        await using (var update = new NpgsqlCommand($"""
            UPDATE resources
            SET source_version = @hash, status = 'ready', last_error = NULL, updated_at = now()
            WHERE resource_id = @id
            RETURNING {ResourceColumns}
            """, conn, tx))
        {
            update.Parameters.AddWithValue("id", resourceId);
            update.Parameters.AddWithValue("hash", contentHash);
            await using var reader = await update.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            resource = ReadResource(reader);
        }

        await tx.CommitAsync(ct);
        return new VersionCommit(resource, applied, applied ? currentVersion : null);
    }

    public async Task<bool> MarkFailedAsync(string tenantId, string resourceId, string error, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE resources SET status = 'failed', last_error = @error, updated_at = now()
            WHERE resource_id = @id AND tenant_id = @tenant
            """);
        cmd.Parameters.AddWithValue("id", resourceId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("error", error);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<CurrentPassage>> GetCurrentPassagesAsync(string tenantId, IReadOnlyCollection<string> nodeIds, CancellationToken ct = default)
    {
        if (nodeIds.Count == 0) return [];

        await using var cmd = dataSource.CreateCommand("""
            SELECT n.node_id, n.resource_id, r.source_version, coalesce(n.original_content, n.title)
            FROM knowledge_nodes n
            JOIN resources r ON r.resource_id = n.resource_id
            WHERE r.tenant_id = @tenant AND r.source_version IS NOT NULL AND n.node_id = ANY(@ids)
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("ids", nodeIds.ToArray());

        var passages = new List<CurrentPassage>(nodeIds.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            passages.Add(new CurrentPassage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return passages;
    }

    private static async Task CopyNodesAsync(NpgsqlConnection conn, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync("""
            COPY knowledge_nodes (node_id, resource_id, parent_node_id, ordinal, level, title, summary, original_content, metadata)
            FROM STDIN (FORMAT BINARY)
            """, ct);

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(node.NodeId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(node.ResourceId, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, node.ParentNodeId, ct);
            await writer.WriteAsync(i, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(node.Level, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(node.Title, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, node.Summary, ct);
            await WriteNullableAsync(writer, node.OriginalContent, ct);
            await writer.WriteAsync(JsonSerializer.Serialize(node.Metadata), NpgsqlDbType.Jsonb, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value is null) await writer.WriteNullAsync(ct);
        else await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private static ResourceReference ReadResource(NpgsqlDataReader reader) => new(
        ResourceId: reader.GetString(0),
        Type: Enum.Parse<SourceType>(reader.GetString(1)),
        Uri: reader.GetString(2),
        SourceVersion: reader.IsDBNull(3) ? null : reader.GetString(3),
        TenantId: reader.GetString(4),
        Status: Enum.Parse<ResourceStatus>(reader.GetString(5), ignoreCase: true));
}
