using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.Ingestion;

/// <summary>
/// Placeholder implementation: registers resources in memory and returns a single
/// document-level node per resource. Replace with AST (Tree-sitter) / structural
/// (Docling) extraction per source type — see system_architecture.md "Format-Aware Processing".
/// </summary>
public sealed class InMemoryResourceIngestionService : IResourceIngestionService
{
    private readonly Dictionary<string, ResourceReference> _resources = new();

    public Task<ResourceReference> RegisterResourceAsync(string uri, SourceType type, string tenantId, CancellationToken ct = default)
    {
        var resource = new ResourceReference(
            ResourceId: Guid.NewGuid().ToString("n"),
            Type: type,
            Uri: uri,
            SourceVersion: "v1",
            TenantId: tenantId);

        _resources[resource.ResourceId] = resource;
        return Task.FromResult(resource);
    }

    public Task<bool> HasChangedAsync(ResourceReference resource, CancellationToken ct = default)
    {
        var known = _resources.TryGetValue(resource.ResourceId, out var existing) ? existing : null;
        return Task.FromResult(known is null || known.SourceVersion != resource.SourceVersion);
    }

    public Task<IReadOnlyList<KnowledgeNode>> IngestAsync(ResourceReference resource, CancellationToken ct = default)
    {
        IReadOnlyList<KnowledgeNode> nodes = new[]
        {
            new KnowledgeNode(
                NodeId: Guid.NewGuid().ToString("n"),
                ResourceId: resource.ResourceId,
                ParentNodeId: null,
                Level: "document",
                Title: resource.Uri,
                Summary: null,
                OriginalContent: null,
                Metadata: new Dictionary<string, string> { ["sourceType"] = resource.Type.ToString() })
        };

        return Task.FromResult(nodes);
    }
}
