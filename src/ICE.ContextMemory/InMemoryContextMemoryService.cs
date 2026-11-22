using System.Collections.Concurrent;
using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.ContextMemory;

/// <summary>
/// Placeholder implementation of the three memory layers (document cache, retrieval
/// cache, session knowledge) backed by process memory. Replace with Redis (see the
/// StackExchange.Redis package reference and infra/docker-compose.yml) so memory
/// survives restarts and is shared across instances — see system_architecture.md
/// "Context Memory &amp; Reuse Engine".
/// </summary>
public sealed class InMemoryContextMemoryService : IContextMemoryService
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<KnowledgeNode>> _documentCache = new();
    private readonly ConcurrentDictionary<string, RetrievalResult> _retrievalCache = new();
    private readonly ConcurrentDictionary<string, SessionKnowledge> _sessionMemory = new();

    public Task<IReadOnlyList<KnowledgeNode>?> GetCachedDocumentKnowledgeAsync(CacheKey key, CancellationToken ct = default)
        => Task.FromResult(_documentCache.TryGetValue(key.ToKeyString(), out var nodes) ? nodes : null);

    public Task SetCachedDocumentKnowledgeAsync(CacheKey key, IReadOnlyList<KnowledgeNode> nodes, CancellationToken ct = default)
    {
        _documentCache[key.ToKeyString()] = nodes;
        return Task.CompletedTask;
    }

    public Task<RetrievalResult?> GetCachedRetrievalAsync(string queryFingerprint, CacheKey key, CancellationToken ct = default)
        => Task.FromResult(_retrievalCache.TryGetValue($"{key.ToKeyString()}:{queryFingerprint}", out var result) ? result : null);

    public Task SetCachedRetrievalAsync(string queryFingerprint, CacheKey key, RetrievalResult result, CancellationToken ct = default)
    {
        _retrievalCache[$"{key.ToKeyString()}:{queryFingerprint}"] = result;
        return Task.CompletedTask;
    }

    public Task<SessionKnowledge?> GetSessionKnowledgeAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(_sessionMemory.TryGetValue(sessionId, out var knowledge) ? knowledge : null);

    public Task SaveSessionKnowledgeAsync(SessionKnowledge knowledge, CancellationToken ct = default)
    {
        _sessionMemory[knowledge.SessionId] = knowledge;
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(CacheKey key, CancellationToken ct = default)
    {
        var prefix = key.ToKeyString();
        _documentCache.TryRemove(prefix, out _);
        foreach (var k in _retrievalCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _retrievalCache.TryRemove(k, out _);
        }
        return Task.CompletedTask;
    }
}
