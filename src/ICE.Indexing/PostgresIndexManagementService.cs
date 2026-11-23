using ICE.Core.Contracts;
using ICE.Core.Models;
using Npgsql;

namespace ICE.Indexing;

/// <summary>
/// Lexical search over the knowledge store using Postgres full-text search. The tsvector is a
/// generated column on knowledge_nodes, so it is always in step with the stored hierarchy.
/// Only passages (nodes with original content) of resources with a committed version are searched.
/// The question's terms are OR-ed rather than AND-ed: a natural-language question rarely has every
/// word in the passage that answers it, and cover-density ranking already favours passages that
/// match more terms close together.
/// </summary>
public sealed class PostgresIndexManagementService(NpgsqlDataSource dataSource) : IIndexManagementService
{
    public async Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default)
    {
        if (kind != IndexKind.Lexical)
        {
            throw new NotSupportedException($"The {kind} index arrives in Phase 2; Phase 1 provides lexical search only.");
        }

        await using var cmd = dataSource.CreateCommand("""
            SELECT n.node_id, n.resource_id, n.original_content, r.source_version,
                   ts_rank_cd(n.search_tsv, q) AS rank
            FROM knowledge_nodes n
            JOIN resources r ON r.resource_id = n.resource_id
            CROSS JOIN LATERAL (
                SELECT replace(plainto_tsquery('english', @query)::text, ' & ', ' | ')::tsquery AS q
            ) terms
            WHERE r.tenant_id = @tenant
              AND r.source_version IS NOT NULL
              AND n.original_content IS NOT NULL
              AND n.search_tsv @@ q
            ORDER BY rank DESC, n.ordinal
            LIMIT @topK
            """);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("topK", topK);

        var results = new List<EvidenceItem>(topK);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new EvidenceItem(
                NodeId: reader.GetString(0),
                ResourceId: reader.GetString(1),
                Content: reader.GetString(2),
                SourceVersion: reader.GetString(3),
                RetrievedVia: IndexKind.Lexical,
                RelevanceScore: reader.GetFloat(4)));
        }
        return results;
    }
}
