using ICE.Core.Models;

namespace ICE.Core.Contracts;

/// <summary>
/// Searches the indexes over the knowledge store: lexical (Postgres full-text search) and semantic
/// (pgvector). Both are maintained as nodes are written, and every search is tenant-scoped and limited
/// to passages of resources with a committed version.
/// </summary>
public interface IIndexManagementService
{
    Task<IReadOnlyList<EvidenceItem>> SearchLexicalAsync(string query, string tenantId, int topK, CancellationToken ct = default);

    /// <summary>Nearest passages by cosine similarity, considering only vectors produced by <paramref name="embeddingModel"/>.</summary>
    Task<IReadOnlyList<EvidenceItem>> SearchSemanticAsync(float[] queryVector, string embeddingModel, string tenantId, int topK, CancellationToken ct = default);
}
