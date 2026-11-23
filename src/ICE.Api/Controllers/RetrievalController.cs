using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ICE.Core.Contracts;
using ICE.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ICE.Api.Controllers;

public record AskRequest(string SessionId, string Question, int TokenBudget = 2000);

[ApiController]
[Route("api/retrieval")]
public sealed partial class RetrievalController : ControllerBase
{
    private const string IndexVersion = "idx-v1";

    private readonly IIntelligentRetrievalService _retrieval;
    private readonly IContextOptimizationService _optimization;
    private readonly IContextMemoryService _memory;
    private readonly IResourceIngestionService _resources;
    private readonly IEvaluationService _evaluation;

    public RetrievalController(
        IIntelligentRetrievalService retrieval,
        IContextOptimizationService optimization,
        IContextMemoryService memory,
        IResourceIngestionService resources,
        IEvaluationService evaluation)
    {
        _retrieval = retrieval;
        _optimization = optimization;
        _memory = memory;
        _resources = resources;
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

    private async Task<bool> AllCurrentAsync(IReadOnlyList<EvidenceItem> evidence, string tenantId, CancellationToken ct)
        => (await KeepCurrentAsync(evidence, tenantId, ct)).Count == evidence.Count;

    private async Task<IReadOnlyList<EvidenceItem>> KeepCurrentAsync(IReadOnlyList<EvidenceItem> evidence, string tenantId, CancellationToken ct)
    {
        var current = new List<EvidenceItem>(evidence.Count);
        foreach (var item in evidence)
        {
            var resource = await _resources.GetResourceAsync(item.ResourceId, ct);
            if (resource is not null && resource.TenantId == tenantId && resource.SourceVersion == item.SourceVersion)
            {
                current.Add(item);
            }
        }
        return current;
    }

    private static string Fingerprint(string question)
    {
        var normalized = Whitespace().Replace(question.Trim().ToLowerInvariant(), " ");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
