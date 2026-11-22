using ICE.Core.Contracts;
using ICE.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ICE.Api.Controllers;

public record AskRequest(string TenantId, string SessionId, string Question, int TokenBudget = 2000);

[ApiController]
[Route("api/retrieval")]
public sealed class RetrievalController : ControllerBase
{
    private readonly IIntelligentRetrievalService _retrieval;
    private readonly IContextOptimizationService _optimization;
    private readonly IEvaluationService _evaluation;

    public RetrievalController(
        IIntelligentRetrievalService retrieval,
        IContextOptimizationService optimization,
        IEvaluationService evaluation)
    {
        _retrieval = retrieval;
        _optimization = optimization;
        _evaluation = evaluation;
    }

    /// <summary>
    /// Runs the progressive retrieval -> context optimization flow for a single question
    /// and returns the minimum sufficient evidence package (not a full-document dump).
    /// </summary>
    [HttpPost("ask")]
    public async Task<ActionResult<ContextPackage>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var query = new RetrievalQuery(Guid.NewGuid().ToString("n"), request.TenantId, request.SessionId, request.Question, request.TokenBudget);

        var result = await _retrieval.RetrieveAsync(query, ct);
        if (!result.EvidenceSufficient)
        {
            result = await _retrieval.ExpandAsync(query, result, ct);
        }

        var contextPackage = await _optimization.BuildContextAsync(result, request.TokenBudget, ct);

        await _evaluation.RecordAsync(new TaskMetrics(
            TaskId: query.QueryId,
            InputTokens: contextPackage.EstimatedTokenCount,
            LatencyMs: (DateTime.UtcNow - started).TotalMilliseconds,
            LlmCallCount: 0,
            RecallAtK: null,
            AnswerAccurate: null,
            CitationAccurate: null,
            CacheHit: false,
            Reprocessed: false), ct);

        return Ok(contextPackage);
    }
}
