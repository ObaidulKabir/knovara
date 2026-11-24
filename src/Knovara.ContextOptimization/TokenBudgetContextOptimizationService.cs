using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.ContextOptimization;

/// <summary>
/// Placeholder implementation: deduplicates by NodeId and truncates to a crude
/// character-based token estimate. Replace with real token budgeting and
/// evidence-selection logic — see system_architecture.md "Context Optimization Service".
/// </summary>
public sealed class TokenBudgetContextOptimizationService : IContextOptimizationService
{
    private const int CharsPerTokenEstimate = 4;

    public Task<ContextPackage> BuildContextAsync(RetrievalResult retrieval, int tokenBudget, CancellationToken ct = default)
    {
        var deduped = retrieval.Evidence
            .GroupBy(e => e.NodeId)
            .Select(g => g.First())
            .ToList();

        var selected = new List<EvidenceItem>();
        var runningTokens = 0;

        foreach (var item in deduped.OrderByDescending(e => e.RelevanceScore))
        {
            var estimatedTokens = item.Content.Length / CharsPerTokenEstimate;
            if (runningTokens + estimatedTokens > tokenBudget) break;

            selected.Add(item);
            runningTokens += estimatedTokens;
        }

        return Task.FromResult(new ContextPackage(retrieval.QueryId, selected, runningTokens));
    }
}
