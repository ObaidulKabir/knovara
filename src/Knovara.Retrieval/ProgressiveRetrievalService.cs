using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.Retrieval;

/// <summary>
/// Retrieval across the modes the benchmark compares (see eval/run_benchmark.py).
/// RetrievalMode.Adaptive is what /ask uses; the others exist to measure what each stage adds.
/// Query understanding, coarse-to-fine retrieval and context expansion are future phases — this
/// runs one search pass per question, widened for the hybrid and rerank modes.
/// </summary>
public sealed class ProgressiveRetrievalService(IIndexManagementService index, IModelService models, RetrievalOptions options)
    : IIntelligentRetrievalService
{
    private const int CandidatePoolSize = 30;
    private const int RerankSemanticCandidates = 20;
    private const int RerankLexicalCandidates = 10;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct = default)
    {
        (IReadOnlyList<EvidenceItem> Evidence, bool Reranked) result = query.Mode switch
        {
            RetrievalMode.Lexical => (await index.SearchLexicalAsync(query.Question, query.TenantId, query.TopK, ct), false),
            RetrievalMode.Semantic => (await SemanticAsync(query, query.TopK, ct), false),
            RetrievalMode.Hybrid => ((await FuseAsync(query, CandidatePoolSize, ct)).Take(query.TopK).ToList(), false),
            RetrievalMode.HybridRerank => (await HybridRerankAsync(query, ct), true),
            RetrievalMode.Adaptive => await AdaptiveAsync(query, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Mode, null)
        };

        return new RetrievalResult(query.QueryId, result.Evidence, EvidenceSufficient: result.Evidence.Count > 0,
            FollowUpSearchHint: null, Reranked: result.Reranked);
    }

    public Task<RetrievalResult> ExpandAsync(RetrievalQuery query, RetrievalResult priorResult, CancellationToken ct = default)
        => Task.FromResult(priorResult);

    private async Task<IReadOnlyList<EvidenceItem>> SemanticAsync(RetrievalQuery query, int topK, CancellationToken ct)
    {
        var model = await models.GetInfoAsync(ct);
        var vector = await models.EmbedQueryAsync(query.Question, ct);
        return await index.SearchSemanticAsync(vector, model.EmbeddingModel, query.TenantId, topK, ct);
    }

    /// <summary>Lexical and semantic candidates merged by Reciprocal Rank Fusion (k=60, a common default).</summary>
    private async Task<List<EvidenceItem>> FuseAsync(RetrievalQuery query, int topK, CancellationToken ct)
    {
        const int rrfK = 60;

        var lexicalTask = index.SearchLexicalAsync(query.Question, query.TenantId, topK, ct);
        var semanticTask = SemanticAsync(query, topK, ct);
        await Task.WhenAll(lexicalTask, semanticTask);

        var byNode = new Dictionary<string, (EvidenceItem Item, double Score)>();
        void Fuse(IReadOnlyList<EvidenceItem> ranked)
        {
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                var item = ranked[rank];
                var score = 1.0 / (rrfK + rank + 1);
                byNode[item.NodeId] = byNode.TryGetValue(item.NodeId, out var existing)
                    ? (existing.Item with { RetrievedVia = IndexKind.Hybrid }, existing.Score + score)
                    : (item, score);
            }
        }
        Fuse(await lexicalTask);
        Fuse(await semanticTask);

        return byNode.Values
            .OrderByDescending(v => v.Score)
            .Select(v => v.Item with { RelevanceScore = v.Score })
            .ToList();
    }

    private async Task<IReadOnlyList<EvidenceItem>> HybridRerankAsync(RetrievalQuery query, CancellationToken ct)
    {
        var semanticTask = SemanticAsync(query, RerankSemanticCandidates, ct);
        var lexicalTask = index.SearchLexicalAsync(query.Question, query.TenantId, RerankLexicalCandidates, ct);
        await Task.WhenAll(semanticTask, lexicalTask);
        return await RerankAsync(query, await semanticTask, await lexicalTask, ct);
    }

    /// <summary>Semantic search first; the lexical search and cross-encoder run only when its top two scores are too close to call.</summary>
    private async Task<(IReadOnlyList<EvidenceItem>, bool)> AdaptiveAsync(RetrievalQuery query, CancellationToken ct)
    {
        var semantic = await SemanticAsync(query, RerankSemanticCandidates, ct);
        var confident = semantic.Count == 1
            || (semantic.Count > 1 && semantic[0].RelevanceScore - semantic[1].RelevanceScore >= options.AdaptiveMarginThreshold);
        if (confident) return (semantic.Take(query.TopK).ToList(), false);

        var lexical = await index.SearchLexicalAsync(query.Question, query.TenantId, RerankLexicalCandidates, ct);
        return (await RerankAsync(query, semantic, lexical, ct), true);
    }

    /// <summary>Pooled without RRF: the cross-encoder judges each candidate, and RRF ranked below semantic alone on the benchmark.</summary>
    private async Task<IReadOnlyList<EvidenceItem>> RerankAsync(
        RetrievalQuery query, IReadOnlyList<EvidenceItem> semanticResults, IReadOnlyList<EvidenceItem> lexicalResults, CancellationToken ct)
    {
        var lexicalIds = lexicalResults.Select(item => item.NodeId).ToHashSet();
        var semantic = semanticResults
            .Select(item => lexicalIds.Contains(item.NodeId) ? item with { RetrievedVia = IndexKind.Hybrid } : item)
            .ToList();
        var semanticIds = semantic.Select(item => item.NodeId).ToHashSet();
        var candidates = semantic.Concat(lexicalResults.Where(item => !semanticIds.Contains(item.NodeId))).ToList();
        if (candidates.Count == 0) return candidates;

        var scores = await models.RerankAsync(query.Question, candidates.Select(c => c.Content).ToList(), ct);
        return candidates
            .Zip(scores, (item, score) => item with { RelevanceScore = score })
            .OrderByDescending(item => item.RelevanceScore)
            .Take(query.TopK)
            .ToList();
    }
}
