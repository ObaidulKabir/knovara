using ICE.Core.Contracts;
using ICE.Core.Models;

namespace ICE.Retrieval;

/// <summary>
/// Placeholder implementation of progressive retrieval: runs a single coarse
/// search pass and treats any result as sufficient. Replace with real query
/// planning, hybrid search + Reciprocal Rank Fusion, reranking, and the
/// coarse -> targeted -> expansion -> sufficiency-check flow described in
/// system_architecture.md "Progressive / Intelligent Retrieval".
/// </summary>
public sealed class ProgressiveRetrievalService : IIntelligentRetrievalService
{
    private readonly IIndexManagementService _index;

    public ProgressiveRetrievalService(IIndexManagementService index)
    {
        _index = index;
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct = default)
    {
        var evidence = await _index.SearchAsync(IndexKind.Lexical, query.Question, query.TenantId, topK: 5, ct);
        return new RetrievalResult(query.QueryId, evidence, EvidenceSufficient: evidence.Count > 0, FollowUpSearchHint: null);
    }

    public Task<RetrievalResult> ExpandAsync(RetrievalQuery query, RetrievalResult priorResult, CancellationToken ct = default)
        => Task.FromResult(priorResult);
}
