using Knovara.ContextOptimization;
using Knovara.Core.Models;

namespace Knovara.Api.Tests;

public class TokenBudgetContextOptimizationServiceTests
{
    private readonly TokenBudgetContextOptimizationService _service = new();

    /// <summary>Content length is divided by 4 for the token estimate, so 4 chars per wanted token.</summary>
    private static EvidenceItem Item(string nodeId, int approxTokens, double score) =>
        new(nodeId, $"res-{nodeId}", nodeId, new string('x', approxTokens * 4), "v1", IndexKind.Semantic, score);

    private async Task<ContextPackage> Build(int budget, params EvidenceItem[] evidence) =>
        await _service.BuildContextAsync(new RetrievalResult("q", evidence, true, null), budget);

    /// <summary>
    /// The regression: evidence is relevance-ordered, so breaking at the first oversized item also
    /// threw away every smaller one behind it — one pinned document emptied the whole package.
    /// </summary>
    [Fact]
    public async Task OversizedItem_IsSkipped_AndSmallerOnesStillFit()
    {
        var package = await Build(100,
            Item("huge", approxTokens: 5000, score: 1.0),   // ranks first, cannot fit
            Item("small-a", approxTokens: 20, score: 0.9),
            Item("small-b", approxTokens: 20, score: 0.8));

        Assert.Equal(new[] { "small-a", "small-b" }, package.Evidence.Select(e => e.NodeId));
        Assert.DoesNotContain(package.Evidence, e => e.NodeId == "huge");
        Assert.True(package.EstimatedTokenCount <= 100);
    }

    [Fact]
    public async Task EverythingFits_KeepsAllInRelevanceOrder()
    {
        var package = await Build(1000,
            Item("mid", 30, 0.5),
            Item("top", 30, 0.9),
            Item("low", 30, 0.1));

        Assert.Equal(new[] { "top", "mid", "low" }, package.Evidence.Select(e => e.NodeId));
    }

    [Fact]
    public async Task StopsAddingOnceBudgetIsSpent()
    {
        var package = await Build(100,
            Item("a", 60, 0.9),
            Item("b", 60, 0.8),   // would exceed the budget alongside a
            Item("c", 30, 0.7));

        Assert.Equal(new[] { "a", "c" }, package.Evidence.Select(e => e.NodeId));
        Assert.True(package.EstimatedTokenCount <= 100);
    }

    [Fact]
    public async Task DeduplicatesByNodeId()
    {
        var package = await Build(1000, Item("dupe", 10, 0.9), Item("dupe", 10, 0.4));

        Assert.Single(package.Evidence);
    }

    [Fact]
    public async Task EveryItemOversized_YieldsEmptyPackage()
    {
        var package = await Build(10, Item("huge-a", 500, 0.9), Item("huge-b", 500, 0.8));

        Assert.Empty(package.Evidence);
        Assert.Equal(0, package.EstimatedTokenCount);
    }
}
