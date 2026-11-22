using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Owns the three memory layers: document knowledge cache, retrieval/evidence cache,
/// and session knowledge memory. Enforces cache invalidation tied to <see cref="CacheKey"/>.
/// </summary>
public interface IContextMemoryService
{
    Task<IReadOnlyList<KnowledgeNode>?> GetCachedDocumentKnowledgeAsync(CacheKey key, CancellationToken ct = default);
    Task SetCachedDocumentKnowledgeAsync(CacheKey key, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default);

    Task<RetrievalResult?> GetCachedRetrievalAsync(string queryFingerprint, CacheKey key, CancellationToken ct = default);
    Task SetCachedRetrievalAsync(string queryFingerprint, CacheKey key, RetrievalResult result, CancellationToken ct = default);

    Task<SessionKnowledge?> GetSessionKnowledgeAsync(string sessionId, CancellationToken ct = default);
    Task SaveSessionKnowledgeAsync(SessionKnowledge knowledge, CancellationToken ct = default);

    /// <summary>Invalidates every cache entry whose key is derived from an outdated source/index/model version.</summary>
    Task InvalidateAsync(CacheKey key, CancellationToken ct = default);
}
