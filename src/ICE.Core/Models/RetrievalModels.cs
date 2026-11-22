namespace ICE.Core.Models;

public enum IndexKind
{
    Semantic,
    Lexical,
    Relationship,
    Structural
}

/// <summary>A unit of evidence returned by retrieval, traceable to its original source.</summary>
public record EvidenceItem(
    string NodeId,
    string ResourceId,
    string Content,
    string SourceVersion,
    IndexKind RetrievedVia,
    double RelevanceScore);

/// <summary>A caller's question plus the context needed to plan retrieval depth.</summary>
public record RetrievalQuery(
    string QueryId,
    string TenantId,
    string SessionId,
    string Question,
    int? MaxTokenBudget = null);

public record RetrievalResult(
    string QueryId,
    IReadOnlyList<EvidenceItem> Evidence,
    bool EvidenceSufficient,
    string? FollowUpSearchHint);

/// <summary>The minimum sufficient, deduplicated evidence package handed to the LLM.</summary>
public record ContextPackage(
    string QueryId,
    IReadOnlyList<EvidenceItem> Evidence,
    int EstimatedTokenCount);
