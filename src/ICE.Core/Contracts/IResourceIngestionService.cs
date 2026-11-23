using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>Result of registering a resource: whether this call introduced a new source version.</summary>
public record ResourceRegistration(
    ResourceReference Resource,
    bool IsNewVersion,
    string? SupersededVersion);

/// <summary>
/// Connects to sources (code, documents, websites), registers each resource once per
/// (tenant, URI), detects new source versions, and runs format-aware extraction.
/// </summary>
public interface IResourceIngestionService
{
    /// <summary>
    /// Registers the resource or returns the existing registration. <c>IsNewVersion</c> is false
    /// when the resource is already registered at <paramref name="sourceVersion"/>, so callers can skip re-ingestion.
    /// </summary>
    Task<ResourceRegistration> RegisterResourceAsync(string uri, SourceType type, string tenantId, string sourceVersion, CancellationToken ct = default);

    Task<ResourceReference?> GetResourceAsync(string resourceId, CancellationToken ct = default);

    /// <summary>Runs format-aware extraction (AST for code, structural extraction for documents/websites).</summary>
    Task<IReadOnlyList<KnowledgeNode>> IngestAsync(ResourceReference resource, CancellationToken ct = default);
}
