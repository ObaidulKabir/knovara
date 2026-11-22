namespace ICE.Core.Models;

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

/// <summary>Cache key components so stale or cross-tenant evidence is never served.</summary>
public record CacheKey(
    string ResourceId,
    string SourceVersion,
    string IndexVersion,
    string ModelVersion,
    string TenantId)
{
    public string ToKeyString() => $"{TenantId}:{ResourceId}:{SourceVersion}:{IndexVersion}:{ModelVersion}";
}
