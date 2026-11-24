using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Knovara.ContextOptimization;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Knovara.Api.Controllers;

public record AskRequest(
    string SessionId,
    string Question,
    int TokenBudget = 2000,
    IReadOnlyList<string>? ResourceIds = null,
    IReadOnlyList<ContextDocument>? Documents = null);
public record SearchRequest(string Question, RetrievalMode Mode = RetrievalMode.Adaptive, int TopK = 5);

[ApiController]
[Route("api/retrieval")]
public sealed partial class RetrievalController : ControllerBase
{
    private const string IndexVersion = "idx-v1";

    /// <summary>
    /// Added above any corpus-retrieved score we have observed (cosine similarity tops out near 1,
    /// cross-encoder logits near a few points either side of 0), so a pinned document still generally
    /// outranks corpus evidence - the user explicitly chose to attach it - while the reranker score
    /// added to it still orders a document's own chunks by their real relevance to the question.
    /// </summary>
    private const double PinnedDocumentBaseScore = 10.0;

    private readonly IIntelligentRetrievalService _retrieval;
    private readonly IContextOptimizationService _optimization;
    private readonly IContextMemoryService _memory;
    private readonly IKnowledgeStore _store;
    private readonly IEvaluationService _evaluation;
    private readonly IModelService _models;
    private readonly ILogger<RetrievalController> _logger;

    public RetrievalController(
        IIntelligentRetrievalService retrieval,
        IContextOptimizationService optimization,
        IContextMemoryService memory,
        IKnowledgeStore store,
        IEvaluationService evaluation,
        IModelService models,
        ILogger<RetrievalController> logger)
    {
        _retrieval = retrieval;
        _optimization = optimization;
        _memory = memory;
        _store = store;
        _evaluation = evaluation;
        _models = models;
        _logger = logger;
    }

    /// <summary>
    /// Evidence cache lookup -> (on miss) progressive retrieval -> verification against current
    /// source versions -> context optimization. Returns the minimum sufficient evidence package.
    /// </summary>
    [HttpPost("ask")]
    public async Task<ActionResult<ContextPackage>> Ask(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] AskRequest request,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var query = new RetrievalQuery(Guid.NewGuid().ToString("n"), tenantId, request.SessionId, request.Question, request.TokenBudget);
        var cacheKey = new EvidenceCacheKey(tenantId, Fingerprint(request.Question), IndexVersion);

        var result = await _memory.GetCachedEvidenceAsync(cacheKey, ct);
        var cacheHit = result is not null && await AllCurrentAsync(result.Evidence, tenantId, ct);

        if (cacheHit)
        {
            result = result! with { QueryId = query.QueryId };
        }
        else
        {
            if (result is not null) await _memory.EvictEvidenceAsync(cacheKey, ct);

            result = await _retrieval.RetrieveAsync(query, ct);
            if (!result.EvidenceSufficient)
            {
                result = await _retrieval.ExpandAsync(query, result, ct);
            }

            result = result with { Evidence = await KeepCurrentAsync(result.Evidence, tenantId, ct) };
            if (result.Evidence.Count > 0)
            {
                await _memory.SetCachedEvidenceAsync(cacheKey, result, ct);
            }
        }

        // Incorporate explicitly added context documents & session knowledge
        var pinnedEvidence = new List<EvidenceItem>();

        if (request.Documents is { Count: > 0 })
        {
            foreach (var doc in request.Documents)
            {
                if (string.IsNullOrWhiteSpace(doc.Content)) continue;

                // Chunked and scored against this question, not pinned whole at a flat score: a large
                // attachment becomes retrievable passages, so when it cannot all fit the token budget,
                // the passages actually relevant to the question are the ones that make it in.
                var chunks = PinnedDocumentChunker.Chunk(doc.Content);
                var scores = await ScoreChunksAsync(request.Question, chunks, ct);
                var resourceId = doc.Source ?? "direct-context-doc";
                var baseTitle = string.IsNullOrWhiteSpace(doc.Title) ? "Added Document" : doc.Title;

                for (var i = 0; i < chunks.Count; i++)
                {
                    pinnedEvidence.Add(new EvidenceItem(
                        NodeId: $"user-doc-{Guid.NewGuid():n}",
                        ResourceId: resourceId,
                        Title: chunks.Count > 1 ? $"{baseTitle} ({i + 1}/{chunks.Count})" : baseTitle,
                        Content: chunks[i],
                        SourceVersion: "current",
                        RetrievedVia: IndexKind.Semantic,
                        RelevanceScore: PinnedDocumentBaseScore + scores[i]));
                }
            }
        }

        var session = await _memory.GetSessionKnowledgeAsync(tenantId, request.SessionId, ct);
        if (session?.Facts is { Count: > 0 })
        {
            foreach (var fact in session.Facts)
            {
                pinnedEvidence.Add(new EvidenceItem(
                    NodeId: $"session-fact-{Guid.NewGuid():n}",
                    ResourceId: fact.Source,
                    Title: "Session Knowledge",
                    Content: fact.Claim,
                    SourceVersion: "current",
                    RetrievedVia: IndexKind.Structural,
                    RelevanceScore: 1.0));
            }
        }

        if (pinnedEvidence.Count > 0)
        {
            var combined = new List<EvidenceItem>(pinnedEvidence);
            if (result?.Evidence != null)
            {
                combined.AddRange(result.Evidence);
            }
            result = (result ?? new RetrievalResult(query.QueryId, [], false, null)) with { Evidence = combined };
        }

        var contextPackage = await _optimization.BuildContextAsync(result!, request.TokenBudget, ct);

        await _evaluation.RecordAsync(new TaskMetrics(
            TaskId: query.QueryId,
            InputTokens: contextPackage.EstimatedTokenCount,
            LatencyMs: (DateTime.UtcNow - started).TotalMilliseconds,
            LlmCallCount: 0,
            RecallAtK: null,
            AnswerAccurate: null,
            CitationAccurate: null,
            CacheHit: cacheHit,
            Reprocessed: false), ct);

        return Ok(contextPackage);
    }

    /// <summary>
    /// Runs one retrieval mode with no caching and no context optimization, so eval/run_benchmark.py can
    /// measure what each stage (lexical, semantic, their fusion, reranking) contributes on its own.
    /// Not used by agents; see <see cref="Ask"/> for that.
    /// </summary>
    [HttpPost("search")]
    public async Task<ActionResult<RetrievalResult>> Search(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] SearchRequest request,
        CancellationToken ct)
    {
        var query = new RetrievalQuery(Guid.NewGuid().ToString("n"), tenantId, SessionId: "benchmark", request.Question, Mode: request.Mode, TopK: request.TopK);
        return Ok(await _retrieval.RetrieveAsync(query, ct));
    }

    /// <summary>
    /// Real relevance to this question, via the same cross-encoder the corpus's HybridRerank/Adaptive
    /// modes use - not assumed. If the model service is unreachable, attaching a document must still
    /// work, so every chunk falls back to an equal score (reading order) rather than failing the ask.
    /// </summary>
    private async Task<IReadOnlyList<double>> ScoreChunksAsync(string question, IReadOnlyList<string> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return [];
        try
        {
            return await _models.RerankAsync(question, chunks, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not score attached-document chunks against the question; keeping them in their original order.");
            return chunks.Select(_ => 0.0).ToList();
        }
    }

    private async Task<bool> AllCurrentAsync(IReadOnlyList<EvidenceItem> evidence, string tenantId, CancellationToken ct)
        => (await KeepCurrentAsync(evidence, tenantId, ct)).Count == evidence.Count;

    /// <summary>
    /// Keeps only evidence whose node still exists at the cited version, and replaces its content with
    /// the original passage from the knowledge store.
    /// </summary>
    private async Task<IReadOnlyList<EvidenceItem>> KeepCurrentAsync(IReadOnlyList<EvidenceItem> evidence, string tenantId, CancellationToken ct)
    {
        var passages = (await _store.GetCurrentPassagesAsync(tenantId, evidence.Select(e => e.NodeId).ToList(), ct))
            .ToDictionary(p => p.NodeId);

        return evidence
            .Where(e => passages.TryGetValue(e.NodeId, out var p) && p.SourceVersion == e.SourceVersion)
            .Select(e => e with { Content = passages[e.NodeId].Content })
            .ToList();
    }

    private static string Fingerprint(string question)
    {
        var normalized = Whitespace().Replace(question.Trim().ToLowerInvariant(), " ");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
