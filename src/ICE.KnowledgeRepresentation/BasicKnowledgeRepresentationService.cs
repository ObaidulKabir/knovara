using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.KnowledgeRepresentation;

/// <summary>
/// Placeholder implementation: returns nodes unchanged and produces a trivial
/// summary. Replace with real hierarchical compression — see
/// system_architecture.md "Hierarchical Knowledge Representation".
/// </summary>
public sealed class BasicKnowledgeRepresentationService : IKnowledgeRepresentationService
{
    public Task<IReadOnlyList<KnowledgeNode>> BuildHierarchyAsync(ResourceReference resource, IReadOnlyList<KnowledgeNode> rawNodes, CancellationToken ct = default)
        => Task.FromResult(rawNodes);

    public Task<string> SummarizeAsync(KnowledgeNode node, IReadOnlyList<KnowledgeNode> children, CancellationToken ct = default)
        => Task.FromResult($"{node.Title}: {children.Count} child node(s).");

    public Task<IReadOnlyList<RelationshipEdge>> ExtractRelationshipsAsync(ResourceReference resource, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RelationshipEdge>>(Array.Empty<RelationshipEdge>());
}
