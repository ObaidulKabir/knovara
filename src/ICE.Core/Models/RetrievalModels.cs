namespace ICE.Core.Models;

public enum IndexKind
{
    Semantic,
    Lexical,
    Hybrid,
    Relationship,
    Structural
}

/// <summary>How retrieval finds candidates. The benchmark compares all four; /ask uses <see cref="HybridRerank"/>.</summary>
public enum RetrievalMode
{
    /// <summary>Postgres full-text search only (Phase 1 behaviour).</summary>
    Lexical,
    /// <summary>Embedding similarity only.</summary>
    Semantic,
    /// <summary>Lexical and semantic results merged with Reciprocal Rank Fusion.</summary>
    Hybrid,
    /// <summary>Hybrid, then the top candidates reordered by a cross-encoder.</summary>
    HybridRerank
}

/// <summary>
/// A unit of evidence returned by retrieval, traceable to its original source.
/// <see cref="Title"/> is the passage's heading path within its document.
/// </summary>
public record EvidenceItem(
    string NodeId,
    string ResourceId,
    string Title,
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
    int? MaxTokenBudget = null,
    RetrievalMode Mode = RetrievalMode.HybridRerank,
    int TopK = 5);

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
