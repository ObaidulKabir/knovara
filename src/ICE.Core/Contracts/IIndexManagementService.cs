using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Maintains the four complementary indexes (semantic/vector, lexical/BM25,
/// relationship/graph, structural/metadata) in sync with the knowledge representation.
/// Every entry is stored with its resource's tenant and source version, and every search is tenant-scoped.
/// </summary>
public interface IIndexManagementService
{
    Task IndexNodeAsync(ResourceReference resource, KnowledgeNode node, IndexingLevel level, CancellationToken ct = default);

    Task IndexRelationshipsAsync(ResourceReference resource, IReadOnlyList<RelationshipEdge> edges, CancellationToken ct = default);

    /// <summary>Removes every index entry for the resource, regardless of version.</summary>
    Task InvalidateAsync(ResourceReference resource, CancellationToken ct = default);

    Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default);
}
