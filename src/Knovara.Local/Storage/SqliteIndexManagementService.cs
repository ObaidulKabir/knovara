using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.Local.Storage;

/// <summary>
/// Lexical and semantic search over the SQLite store: the local runtime's counterpart of PostgresIndexManagementService.
/// Lexical search is FTS5 with bm25 ranking; semantic search is an exact cosine scan over the float32 vectors, which is
/// fast enough at local corpus sizes and avoids an approximate index whose recall would have to be measured separately.
/// Only passages of resources with a committed version are searched, and every search is tenant-scoped.
/// </summary>
public sealed class SqliteIndexManagementService(SqliteDatabase db) : IIndexManagementService
{
    // Title matches count for less than body matches, as with the weighted tsvector on the server path.
    private const string Bm25 = "bm25(knowledge_fts, 0.4, 1.0)";

    // The Postgres 'english' configuration drops these before matching; FTS5's tokenizer does not, so do it here.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is", "it", "no", "not", "of", "on",
        "or", "such", "that", "the", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with", "what",
        "which", "who", "how", "do", "does", "did", "can", "i", "we", "you", "our", "your", "from", "has", "have", "had"
    };

    /// <summary>
    /// The question's terms are OR-ed rather than AND-ed: a natural-language question rarely has every word in the passage
    /// that answers it, and bm25 already ranks passages that match more terms higher.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceItem>> SearchLexicalAsync(string query, string tenantId, int topK, CancellationToken ct = default)
    {
        var match = BuildMatchExpression(query);
        if (match is null) return [];

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.node_id, n.resource_id, n.title, n.original_content, r.source_version, -{Bm25} AS score
            FROM knowledge_fts
            JOIN knowledge_nodes n ON n.rowid = knowledge_fts.rowid
            JOIN resources r ON r.resource_id = n.resource_id
            WHERE knowledge_fts MATCH @match
              AND r.tenant_id = @tenant
              AND r.source_version IS NOT NULL
              AND n.original_content IS NOT NULL
            ORDER BY score DESC, n.ordinal
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@match", match);
        cmd.Parameters.AddWithValue("@tenant", tenantId);
        cmd.Parameters.AddWithValue("@topK", topK);

        var results = new List<EvidenceItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new EvidenceItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), IndexKind.Lexical, reader.GetDouble(5)));
        }
        return results;
    }

    public async Task<IReadOnlyList<EvidenceItem>> SearchSemanticAsync(float[] queryVector, string embeddingModel, string tenantId, int topK, CancellationToken ct = default)
    {
        if (topK <= 0 || queryVector.Length == 0) return [];

        // Pass 1: scan vectors only, keeping the best topK in a min-heap so the passage text of the rest is never read.
        var best = new PriorityQueue<string, float>();
        await using var conn = await db.OpenAsync(ct);
        await using (var scan = conn.CreateCommand())
        {
            scan.CommandText = """
                SELECT n.node_id, n.embedding
                FROM knowledge_nodes n
                JOIN resources r ON r.resource_id = n.resource_id
                WHERE r.tenant_id = @tenant
                  AND r.source_version IS NOT NULL
                  AND n.original_content IS NOT NULL
                  AND n.embedding_model = @model
                  AND n.embedding IS NOT NULL
                """;
            scan.Parameters.AddWithValue("@tenant", tenantId);
            scan.Parameters.AddWithValue("@model", embeddingModel);

            await using var reader = await scan.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var blob = (byte[])reader.GetValue(1);
                var vector = MemoryMarshal.Cast<byte, float>(blob);
                if (vector.Length != queryVector.Length) continue;

                var score = TensorPrimitives.CosineSimilarity(queryVector, vector);
                if (best.Count < topK) best.Enqueue(reader.GetString(0), score);
                else if (best.TryPeek(out _, out var worst) && score > worst) best.EnqueueDequeue(reader.GetString(0), score);
            }
        }

        var scored = new Dictionary<string, float>();
        while (best.TryDequeue(out var id, out var s)) scored[id] = s;
        if (scored.Count == 0) return [];

        // Pass 2: fetch text for the winners only.
        var results = new List<EvidenceItem>(scored.Count);
        await using (var fetch = conn.CreateCommand())
        {
            var names = scored.Keys.Select((id, i) => { fetch.Parameters.AddWithValue($"@n{i}", id); return $"@n{i}"; });
            fetch.CommandText = $"""
                SELECT n.node_id, n.resource_id, n.title, n.original_content, r.source_version
                FROM knowledge_nodes n JOIN resources r ON r.resource_id = n.resource_id
                WHERE n.node_id IN ({string.Join(",", names)})
                """;
            await using var reader = await fetch.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                results.Add(new EvidenceItem(id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    IndexKind.Semantic, scored[id]));
            }
        }
        return results.OrderByDescending(e => e.RelevanceScore).ToList();
    }

    /// <summary>
    /// Turns free text into an FTS5 expression: words only (so punctuation can never be read as FTS syntax), stop words
    /// removed, each term quoted and OR-ed. Returns null when nothing searchable is left.
    /// </summary>
    internal static string? BuildMatchExpression(string query)
    {
        var terms = new List<string>();
        var word = new StringBuilder();

        void Flush()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            word.Clear();
            if (!StopWords.Contains(w) && !terms.Contains(w, StringComparer.OrdinalIgnoreCase)) terms.Add(w);
        }

        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch)) word.Append(char.ToLowerInvariant(ch));
            else Flush();
        }
        Flush();

        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => $"\"{t}\""));
    }
}
