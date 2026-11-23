using System.Collections.Concurrent;
using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.Ingestion;

/// <summary>
/// Placeholder implementation: registers resources in memory and returns a single
/// document-level node per resource. Replace extraction with AST (Tree-sitter) /
/// structural (Docling) processing per source type — see system_architecture.md
/// "Format-Aware Processing".
/// </summary>
public sealed class InMemoryResourceIngestionService : IResourceIngestionService
{
    private readonly ConcurrentDictionary<string, ResourceReference> _byId = new();
    private readonly ConcurrentDictionary<(string TenantId, string Uri), string> _idByTenantUri = new();
    private readonly object _registrationLock = new();

    public Task<ResourceRegistration> RegisterResourceAsync(string uri, SourceType type, string tenantId, string sourceVersion, CancellationToken ct = default)
    {
        lock (_registrationLock)
        {
            if (_idByTenantUri.TryGetValue((tenantId, uri), out var existingId))
            {
                var existing = _byId[existingId];
                if (existing.SourceVersion == sourceVersion)
                {
                    return Task.FromResult(new ResourceRegistration(existing, IsNewVersion: false, SupersededVersion: null));
                }

                var updated = existing with { SourceVersion = sourceVersion };
                _byId[existingId] = updated;
                return Task.FromResult(new ResourceRegistration(updated, IsNewVersion: true, SupersededVersion: existing.SourceVersion));
            }

            var resource = new ResourceReference(
                ResourceId: Guid.NewGuid().ToString("n"),
                Type: type,
                Uri: uri,
                SourceVersion: sourceVersion,
                TenantId: tenantId);

            _byId[resource.ResourceId] = resource;
            _idByTenantUri[(tenantId, uri)] = resource.ResourceId;
            return Task.FromResult(new ResourceRegistration(resource, IsNewVersion: true, SupersededVersion: null));
        }
    }

    public Task<ResourceReference?> GetResourceAsync(string resourceId, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(resourceId, out var resource) ? resource : null);

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
