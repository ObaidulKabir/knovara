using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Builds the hierarchical knowledge representation (document -> chapter/module ->
/// section/class -> topic -> original passage) and extracts entities/relationships.
/// </summary>
public interface IKnowledgeRepresentationService
{
    Task<IReadOnlyList<KnowledgeNode>> BuildHierarchyAsync(ResourceReference resource, IReadOnlyList<KnowledgeNode> rawNodes, CancellationToken ct = default);

    /// <summary>Generates a summary for a node from its children (recomputed only for affected parents on change).</summary>
    Task<string> SummarizeAsync(KnowledgeNode node, IReadOnlyList<KnowledgeNode> children, CancellationToken ct = default);

    Task<IReadOnlyList<RelationshipEdge>> ExtractRelationshipsAsync(ResourceReference resource, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default);
}
