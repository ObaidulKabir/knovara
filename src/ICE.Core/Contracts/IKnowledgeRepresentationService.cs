using ICE.Core.Models;

namespace ICE.Core.Contracts;

public sealed class HierarchyValidationException(string message) : Exception(message);

/// <summary>
/// Turns an extractor's output into the stored hierarchy: validates its structure and assigns
/// store-wide node ids. Summaries and relationship extraction arrive in Phase 2.
/// </summary>
public interface IKnowledgeRepresentationService
{
    /// <exception cref="HierarchyValidationException">The extracted nodes do not form a valid hierarchy.</exception>
    IReadOnlyList<KnowledgeNode> BuildHierarchy(ResourceReference resource, IReadOnlyList<ExtractedNode> extracted);
}
