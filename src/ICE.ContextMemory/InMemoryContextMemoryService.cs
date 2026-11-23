using System.Collections.Concurrent;
using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.ContextMemory;

/// <summary>
/// Placeholder implementation of the three memory layers (document cache, evidence
/// cache, session knowledge) backed by process memory. Replace with Redis (see
/// infra/docker-compose.yml) so memory survives restarts and is shared across
/// instances — see system_architecture.md "Context Memory &amp; Reuse Engine".
/// </summary>
public sealed class InMemoryContextMemoryService : IContextMemoryService
{
    private static readonly TimeSpan EvidenceTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<CacheKey, IReadOnlyList<KnowledgeNode>> _documentCache = new();
    private readonly ConcurrentDictionary<string, (RetrievalResult Result, DateTimeOffset StoredAt)> _evidenceCache = new();
    private readonly ConcurrentDictionary<(string TenantId, string SessionId), SessionKnowledge> _sessionMemory = new();

    public Task<IReadOnlyList<KnowledgeNode>?> GetCachedDocumentKnowledgeAsync(CacheKey key, CancellationToken ct = default)
        => Task.FromResult(_documentCache.TryGetValue(key, out var nodes) ? nodes : null);

    public Task SetCachedDocumentKnowledgeAsync(CacheKey key, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        _documentCache[key] = nodes;
        return Task.CompletedTask;
    }

    public Task<RetrievalResult?> GetCachedEvidenceAsync(EvidenceCacheKey key, CancellationToken ct = default)
    {
        var k = key.ToKeyString();
        if (!_evidenceCache.TryGetValue(k, out var entry)) return Task.FromResult<RetrievalResult?>(null);

        if (DateTimeOffset.UtcNow - entry.StoredAt > EvidenceTtl)
        {
            _evidenceCache.TryRemove(k, out _);
            return Task.FromResult<RetrievalResult?>(null);
        }

        return Task.FromResult<RetrievalResult?>(entry.Result);
    }

    public Task SetCachedEvidenceAsync(EvidenceCacheKey key, RetrievalResult result, CancellationToken ct = default)
    {
        _evidenceCache[key.ToKeyString()] = (result, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task EvictEvidenceAsync(EvidenceCacheKey key, CancellationToken ct = default)
    {
        _evidenceCache.TryRemove(key.ToKeyString(), out _);
        return Task.CompletedTask;
    }

    public Task<SessionKnowledge?> GetSessionKnowledgeAsync(string tenantId, string sessionId, CancellationToken ct = default)
        => Task.FromResult(_sessionMemory.TryGetValue((tenantId, sessionId), out var knowledge) ? knowledge : null);

    public Task SaveSessionKnowledgeAsync(string tenantId, SessionKnowledge knowledge, CancellationToken ct = default)
    {
        _sessionMemory[(tenantId, knowledge.SessionId)] = knowledge;
        return Task.CompletedTask;
    }

    public Task PurgeSupersededAsync(string tenantId, string resourceId, string supersededVersion, CancellationToken ct = default)
    {
        foreach (var key in _documentCache.Keys.Where(k =>
                     k.TenantId == tenantId && k.ResourceId == resourceId && k.SourceVersion == supersededVersion).ToList())
        {
            _documentCache.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}
