using Knovara.Core.Models;

namespace Knovara.Core.Contracts;

/// <summary>
/// Deduplicates retrieved evidence, applies token budgeting, and assembles the
/// minimum sufficient context package for the reasoning model.
/// </summary>
public interface IContextOptimizationService
{
    Task<ContextPackage> BuildContextAsync(RetrievalResult retrieval, int tokenBudget, CancellationToken ct = default);
}
