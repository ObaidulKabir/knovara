using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.KnowledgeRepresentation;

/// <summary>
/// Validates an extractor's node list and maps its local ids to store-wide ids, so an extractor
/// can never overwrite or link to nodes it did not produce.
/// </summary>
public sealed class HierarchyBuilder : IKnowledgeRepresentationService
{
    public const int MaxNodes = 50_000;
    public const int MaxTitleLength = 1_000;
    public const int MaxContentLength = 100_000;
    public const int MaxLevelLength = 40;
    public const int MaxMetadataEntries = 20;

    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    public IReadOnlyList<KnowledgeNode> BuildHierarchy(ResourceReference resource, IReadOnlyList<ExtractedNode> extracted)
    {
        if (extracted.Count == 0) throw new HierarchyValidationException("At least one node (the document root) is required.");
        if (extracted.Count > MaxNodes) throw new HierarchyValidationException($"At most {MaxNodes} nodes are allowed.");
        if (extracted[0].ParentLocalId is not null) throw new HierarchyValidationException("The first node must be the document root.");

        var ids = new Dictionary<string, string>(extracted.Count, StringComparer.Ordinal);
        var nodes = new List<KnowledgeNode>(extracted.Count);

        for (var i = 0; i < extracted.Count; i++)
        {
            var node = extracted[i];
            var where = $"Node {i} ('{node.LocalId}')";

            if (string.IsNullOrWhiteSpace(node.LocalId)) throw new HierarchyValidationException($"Node {i} has no localId.");
            if (ids.ContainsKey(node.LocalId)) throw new HierarchyValidationException($"{where} repeats a localId.");
            if (i > 0 && node.ParentLocalId is null) throw new HierarchyValidationException($"{where} is a second root; only the first node may have no parent.");
            if (string.IsNullOrWhiteSpace(node.Level) || node.Level.Length > MaxLevelLength) throw new HierarchyValidationException($"{where} has a missing or too-long level.");
            if (string.IsNullOrWhiteSpace(node.Title) || node.Title.Length > MaxTitleLength) throw new HierarchyValidationException($"{where} has a missing or too-long title.");
            if (node.Content is { Length: > MaxContentLength }) throw new HierarchyValidationException($"{where} has content longer than {MaxContentLength} characters.");
            if (node.Metadata is { Count: > MaxMetadataEntries }) throw new HierarchyValidationException($"{where} has more than {MaxMetadataEntries} metadata entries.");

            string? parentId = null;
            if (node.ParentLocalId is not null && !ids.TryGetValue(node.ParentLocalId, out parentId))
            {
                throw new HierarchyValidationException($"{where} refers to parent '{node.ParentLocalId}', which does not appear before it.");
            }

            var nodeId = Guid.NewGuid().ToString("n");
            ids[node.LocalId] = nodeId;
            nodes.Add(new KnowledgeNode(
                NodeId: nodeId,
                ResourceId: resource.ResourceId,
                ParentNodeId: parentId,
                Level: node.Level,
                Title: node.Title,
                Summary: null,
                OriginalContent: string.IsNullOrWhiteSpace(node.Content) ? null : node.Content,
                Metadata: node.Metadata ?? NoMetadata));
        }

        return nodes;
    }
}
