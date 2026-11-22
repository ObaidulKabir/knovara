using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Deduplicates retrieved evidence, applies token budgeting, and assembles the
/// minimum sufficient context package for the reasoning model.
/// </summary>
public interface IContextOptimizationService
{
    Task<ContextPackage> BuildContextAsync(RetrievalResult retrieval, int tokenBudget, CancellationToken ct = default);
}
