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
    private sealed record IndexedNode(string TenantId, string SourceVersion, KnowledgeNode Node);

    private readonly List<IndexedNode> _nodes = new();
    private readonly List<(string TenantId, RelationshipEdge Edge)> _edges = new();
    private readonly object _lock = new();

    public Task IndexNodeAsync(ResourceReference resource, KnowledgeNode node, IndexingLevel level, CancellationToken ct = default)
    {
        lock (_lock) _nodes.Add(new IndexedNode(resource.TenantId, resource.SourceVersion, node));
        return Task.CompletedTask;
    }

    public Task IndexRelationshipsAsync(ResourceReference resource, IReadOnlyList<RelationshipEdge> edges, CancellationToken ct = default)
    {
        lock (_lock) _edges.AddRange(edges.Select(e => (resource.TenantId, e)));
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(ResourceReference resource, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var removedNodeIds = _nodes
                .Where(n => n.Node.ResourceId == resource.ResourceId)
                .Select(n => n.Node.NodeId)
                .ToHashSet();
            _nodes.RemoveAll(n => n.Node.ResourceId == resource.ResourceId);
            _edges.RemoveAll(e => removedNodeIds.Contains(e.Edge.FromNodeId) || removedNodeIds.Contains(e.Edge.ToNodeId));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default)
    {
        List<EvidenceItem> results;
        lock (_lock)
        {
            results = _nodes
                .Where(n => n.TenantId == tenantId)
                .Where(n => n.Node.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || (n.Node.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(topK)
                .Select(n => new EvidenceItem(
                    n.Node.NodeId,
                    n.Node.ResourceId,
                    n.Node.OriginalContent ?? n.Node.Summary ?? n.Node.Title,
                    n.SourceVersion,
                    kind,
                    RelevanceScore: 1.0))
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<EvidenceItem>>(results);
    }
}
