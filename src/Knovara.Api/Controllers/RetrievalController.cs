using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Knovara.Api.Controllers;

public record AskRequest(string SessionId, string Question, int TokenBudget = 2000);
public record SearchRequest(string Question, RetrievalMode Mode = RetrievalMode.Adaptive, int TopK = 5);

[ApiController]
[Route("api/retrieval")]
public sealed partial class RetrievalController : ControllerBase
{
    private const string IndexVersion = "idx-v1";

    private readonly IIntelligentRetrievalService _retrieval;
    private readonly IContextOptimizationService _optimization;
    private readonly IContextMemoryService _memory;
    private readonly IKnowledgeStore _store;
    private readonly IEvaluationService _evaluation;

    public RetrievalController(
        IIntelligentRetrievalService retrieval,
        IContextOptimizationService optimization,
        IContextMemoryService memory,
        IKnowledgeStore store,
        IEvaluationService evaluation)
    {
        _retrieval = retrieval;
        _optimization = optimization;
        _memory = memory;
        _store = store;
        _evaluation = evaluation;
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
