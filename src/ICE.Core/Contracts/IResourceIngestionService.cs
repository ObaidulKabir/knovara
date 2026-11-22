using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Connects to sources (code, documents, websites), detects whether a source version
/// has changed, and triggers format-aware extraction only for new or changed material.
/// </summary>
public interface IResourceIngestionService
{
    Task<ResourceReference> RegisterResourceAsync(string uri, SourceType type, string tenantId, CancellationToken ct = default);

    /// <summary>Returns true if the resource's content has changed since the last recorded SourceVersion.</summary>
    Task<bool> HasChangedAsync(ResourceReference resource, CancellationToken ct = default);

    /// <summary>Runs format-aware extraction (AST for code, structural extraction for documents/websites).</summary>
    Task<IReadOnlyList<KnowledgeNode>> IngestAsync(ResourceReference resource, CancellationToken ct = default);
}
