using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.Indexing;

/// <summary>
/// Placeholder implementation: keeps nodes in an in-memory list and does naive
/// substring search instead of real semantic/BM25/graph/metadata indexes.
/// Replace with pgvector (semantic), Postgres full-text search (lexical),
/// Postgres graph tables (relationship) — see system_architecture.md
/// "Multi-Dimensional Indexing" and the recommended technology stack.
/// </summary>
public sealed class InMemoryIndexManagementService : IIndexManagementService
{
    private readonly List<KnowledgeNode> _nodes = new();
    private readonly List<RelationshipEdge> _edges = new();

    public Task IndexNodeAsync(KnowledgeNode node, IndexingLevel level, CancellationToken ct = default)
    {
        _nodes.Add(node);
        return Task.CompletedTask;
    }

    public Task IndexRelationshipsAsync(IReadOnlyList<RelationshipEdge> edges, CancellationToken ct = default)
    {
        _edges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(ResourceReference resource, CancellationToken ct = default)
    {
        _nodes.RemoveAll(n => n.ResourceId == resource.ResourceId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default)
    {
        var results = _nodes
            .Where(n => (n.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (n.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(topK)
            .Select(n => new EvidenceItem(n.NodeId, n.ResourceId, n.OriginalContent ?? n.Summary ?? n.Title, "v1", kind, RelevanceScore: 1.0))
            .ToList();

        return Task.FromResult<IReadOnlyList<EvidenceItem>>(results);
    }
}
