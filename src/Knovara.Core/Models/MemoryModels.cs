namespace Knovara.Core.Models;

public enum FactStatus
{
    Unverified,
    Verified,
    Contradicted
}

public record Fact(
    string Claim,
    string Source,
    FactStatus Status);

/// <summary>Per-session reusable knowledge state: what has been learned, examined, and left unresolved.</summary>
public record SessionKnowledge(
    string SessionId,
    string ResourceId,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<string> PreviouslyExamined,
    IReadOnlyList<string> UnresolvedQuestions);

/// <summary>
/// Evidence-cache key. Looked up before retrieval, when the question is not yet tied to any
/// resource, so it cannot include a source version; hits are validated against current versions instead.
/// </summary>
public record EvidenceCacheKey(
    string TenantId,
    string QueryFingerprint,
    string IndexVersion)
{
    public string ToKeyString() => $"{TenantId}:{IndexVersion}:{QueryFingerprint}";
}

/// <summary>Document-cache key: pins tenant, resource and every version the cached knowledge depends on.</summary>
public record CacheKey(
    string ResourceId,
    string SourceVersion,
    string IndexVersion,
    string ModelVersion,
    string TenantId)
{
    public string ToKeyString() => $"{TenantId}:{ResourceId}:{SourceVersion}:{IndexVersion}:{ModelVersion}";
}
