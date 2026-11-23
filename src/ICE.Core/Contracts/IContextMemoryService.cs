using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Owns the three memory layers: document knowledge cache (keyed by <see cref="CacheKey"/>),
/// evidence cache (keyed by <see cref="EvidenceCacheKey"/>), and session knowledge (keyed by tenant + session id).
/// </summary>
public interface IContextMemoryService
{
    Task<IReadOnlyList<KnowledgeNode>?> GetCachedDocumentKnowledgeAsync(CacheKey key, CancellationToken ct = default);
    Task SetCachedDocumentKnowledgeAsync(CacheKey key, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default);

    /// <summary>
    /// Returns cached evidence for an equivalent question. Callers must still check each item's
    /// <see cref="EvidenceItem.SourceVersion"/> against the current resource version before using it.
    /// </summary>
    Task<RetrievalResult?> GetCachedEvidenceAsync(EvidenceCacheKey key, CancellationToken ct = default);
    Task SetCachedEvidenceAsync(EvidenceCacheKey key, RetrievalResult result, CancellationToken ct = default);
    Task EvictEvidenceAsync(EvidenceCacheKey key, CancellationToken ct = default);

    Task<SessionKnowledge?> GetSessionKnowledgeAsync(string tenantId, string sessionId, CancellationToken ct = default);
    Task SaveSessionKnowledgeAsync(string tenantId, SessionKnowledge knowledge, CancellationToken ct = default);

    /// <summary>Purges document-cache entries for a superseded source version.</summary>
    Task PurgeSupersededAsync(string tenantId, string resourceId, string supersededVersion, CancellationToken ct = default);
}
