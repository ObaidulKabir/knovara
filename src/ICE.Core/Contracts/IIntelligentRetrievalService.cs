using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Plans a query, executes progressive multi-index retrieval (coarse -> targeted -> expansion),
/// reranks candidates via Reciprocal Rank Fusion, and checks evidence sufficiency before
/// expanding further, under a bounded search budget.
/// </summary>
public interface IIntelligentRetrievalService
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct = default);

    /// <summary>Expands context (neighbors, callers, related sections) only when prior evidence was insufficient.</summary>
    Task<RetrievalResult> ExpandAsync(RetrievalQuery query, RetrievalResult priorResult, CancellationToken ct = default);
}
