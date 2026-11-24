using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Npgsql;

namespace Knovara.Indexing;

/// <summary>
/// Lexical and semantic search over stored passages. Both indexes live on knowledge_nodes (a generated
/// tsvector column and a pgvector column with an HNSW index), so they are always in step with the
/// stored hierarchy. Only passages of resources with a committed version are searched.
/// </summary>
public sealed class PostgresIndexManagementService(NpgsqlDataSource dataSource) : IIndexManagementService
{
    private const string PassageColumns = "n.node_id, n.resource_id, n.title, n.original_content, r.source_version";

    /// <summary>
    /// The question's terms are OR-ed rather than AND-ed: a natural-language question rarely has every word
    /// in the passage that answers it, and cover-density ranking already favours passages that match more
    /// terms close together.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceItem>> SearchLexicalAsync(string query, string tenantId, int topK, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {PassageColumns}, ts_rank_cd(n.search_tsv, q) AS score
            FROM knowledge_nodes n
            JOIN resources r ON r.resource_id = n.resource_id
            CROSS JOIN LATERAL (
                SELECT replace(plainto_tsquery('english', @query)::text, ' & ', ' | ')::tsquery AS q
            ) terms
            WHERE r.tenant_id = @tenant
              AND r.source_version IS NOT NULL
              AND n.original_content IS NOT NULL
              AND n.search_tsv @@ q
            ORDER BY score DESC, n.ordinal
            LIMIT @topK
            """);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("topK", topK);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadEvidenceAsync(reader, IndexKind.Lexical, ct);
    }

    public async Task<IReadOnlyList<EvidenceItem>> SearchSemanticAsync(float[] queryVector, string embeddingModel, string tenantId, int topK, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The tenant filter is applied after the HNSW scan; iterative scanning keeps fetching candidates
        // until topK rows pass the filter, instead of returning too few.
        await using (var settings = new NpgsqlCommand(
            "SET LOCAL hnsw.iterative_scan = relaxed_order; SET LOCAL hnsw.ef_search = 100", conn, tx))
        {
            await settings.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand($"""
            SELECT {PassageColumns}, 1 - (n.embedding <=> @vector::vector) AS score
            FROM knowledge_nodes n
            JOIN resources r ON r.resource_id = n.resource_id
            WHERE r.tenant_id = @tenant
              AND r.source_version IS NOT NULL
              AND n.original_content IS NOT NULL
              AND n.embedding_model = @model
            ORDER BY n.embedding <=> @vector::vector
            LIMIT @topK
            """, conn, tx);
        cmd.Parameters.AddWithValue("vector", PgVector.ToText(queryVector));
        cmd.Parameters.AddWithValue("model", embeddingModel);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("topK", topK);

        List<EvidenceItem> results;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            results = await ReadEvidenceAsync(reader, IndexKind.Semantic, ct);
        }
        await tx.CommitAsync(ct);

        // relaxed_order can return near-ties slightly out of order.
        return results.OrderByDescending(e => e.RelevanceScore).ToList();
    }

    private static async Task<List<EvidenceItem>> ReadEvidenceAsync(NpgsqlDataReader reader, IndexKind kind, CancellationToken ct)
    {
        var results = new List<EvidenceItem>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new EvidenceItem(
                NodeId: reader.GetString(0),
                ResourceId: reader.GetString(1),
                Title: reader.GetString(2),
                Content: reader.GetString(3),
                SourceVersion: reader.GetString(4),
                RetrievedVia: kind,
                RelevanceScore: Convert.ToDouble(reader.GetValue(5))));
        }
        return results;
    }
}
