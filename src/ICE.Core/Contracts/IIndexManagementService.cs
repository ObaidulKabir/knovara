using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Maintains the four complementary indexes (semantic/vector, lexical/BM25,
/// relationship/graph, structural/metadata) in sync with the knowledge representation.
/// </summary>
public interface IIndexManagementService
{
    Task IndexNodeAsync(KnowledgeNode node, IndexingLevel level, CancellationToken ct = default);

    Task IndexRelationshipsAsync(IReadOnlyList<RelationshipEdge> edges, CancellationToken ct = default);

    /// <summary>Invalidates only the index entries dependent on a changed resource version.</summary>
    Task InvalidateAsync(ResourceReference resource, CancellationToken ct = default);

    Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default);
}
