using Knovara.Core.Models;

namespace Knovara.Core.Contracts;

public enum VersionCheck
{
    NotFound,
    Unchanged,
    Changed
}

/// <summary>Outcome of committing an extraction. <c>Applied</c> is false when the version was already current.</summary>
public record VersionCommit(
    ResourceReference Resource,
    bool Applied,
    string? SupersededVersion);

/// <summary>
/// The Resource &amp; Knowledge Store (D1): resources with their current version, and the hierarchy of
/// that version. Every operation is scoped to a tenant.
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Registers the resource once per (tenant, URI), or returns the existing one, and marks it pending.</summary>
    Task<ResourceReference> RegisterResourceAsync(string tenantId, string uri, SourceType type, CancellationToken ct = default);

    Task<ResourceReference?> GetResourceAsync(string tenantId, string resourceId, CancellationToken ct = default);

    /// <summary>The tenant's resources matching <paramref name="filter"/>, most recently updated first, one page at a time.</summary>
    Task<ResourcePage> QueryResourcesAsync(string tenantId, ResourceFilter filter, CancellationToken ct = default);

    /// <summary>Counts of what the tenant has stored, by status and type, plus passage/embedding/byte totals.</summary>
    Task<ResourceStats> GetStatsAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The extracted passages of the resource's current version, in reading order. Null if the resource does not exist for this tenant.</summary>
    Task<PassagePage?> ListPassagesAsync(string tenantId, string resourceId, int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// Deletes the resource and its entire knowledge hierarchy (knowledge_nodes and relationship_edges
    /// cascade from the FK in init.sql). Returns false if it did not exist for this tenant.
    /// </summary>
    Task<bool> DeleteResourceAsync(string tenantId, string resourceId, CancellationToken ct = default);

    /// <summary>
    /// Deletes several resources in one statement, selected by explicit ids and/or by status, and returns the ids
    /// actually deleted. At least one selector is required, so this can never mean "everything" by accident.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteResourcesAsync(
        string tenantId, IReadOnlyCollection<string>? resourceIds, ResourceStatus? status, CancellationToken ct = default);

    /// <summary>
    /// Registers (or re-registers) a document by file name and stores its bytes, so the worker can fetch them from the API.
    /// Uploading the same name again replaces the content and marks the resource pending for re-processing.
    /// </summary>
    Task<ResourceReference> SaveUploadAsync(string tenantId, string fileName, byte[] content, CancellationToken ct = default);

    Task<StoredUpload?> GetUploadAsync(string tenantId, string resourceId, CancellationToken ct = default);

    /// <summary>
    /// Compares a freshly computed content hash with the stored version. The resource counts as unchanged
    /// only if the hash matches and every passage is already embedded with <paramref name="embeddingModel"/>;
    /// then it is marked ready so the extractor can stop before parsing. Otherwise it is marked processing.
    /// </summary>
    Task<VersionCheck> CheckVersionAsync(string tenantId, string resourceId, string contentHash, string embeddingModel, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces the resource's hierarchy with <paramref name="nodes"/>, stores each passage's
    /// embedding from <paramref name="embeddings"/> (keyed by node id), and records the new version.
    /// </summary>
    Task<VersionCommit?> CommitVersionAsync(
        string tenantId,
        string resourceId,
        string contentHash,
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyDictionary<string, float[]> embeddings,
        string embeddingModel,
        CancellationToken ct = default);

    Task<bool> MarkFailedAsync(string tenantId, string resourceId, string error, CancellationToken ct = default);

    /// <summary>Returns the current text and version of each node that still exists for the tenant.</summary>
    Task<IReadOnlyList<CurrentPassage>> GetCurrentPassagesAsync(string tenantId, IReadOnlyCollection<string> nodeIds, CancellationToken ct = default);
}
