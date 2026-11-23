using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Searches the indexes over the knowledge store. Phase 1 provides the lexical index (Postgres
/// full-text search, maintained automatically as nodes are written); semantic, relationship and
/// structural indexes arrive in Phase 2. Every search is tenant-scoped.
/// </summary>
public interface IIndexManagementService
{
    Task<IReadOnlyList<EvidenceItem>> SearchAsync(IndexKind kind, string query, string tenantId, int topK, CancellationToken ct = default);
}
