using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.Core.Ingestion;

public enum ExtractionStatus
{
    NotFound,
    /// <summary>The extractor's hierarchy was rejected; the resource has been marked failed.</summary>
    Invalid,
    Committed
}

public record ExtractionOutcome(ExtractionStatus Status, ResourceReference? Resource = null, string? Error = null);

/// <summary>
/// Turns an extractor's node list into stored knowledge: validates the hierarchy, embeds the passages, commits the version
/// and refreshes context memory. Shared by the API endpoint the Docling worker calls and by the local runtime's in-process
/// worker, so both ingest paths store exactly the same thing.
/// </summary>
public sealed class ExtractionCommitter(
    IKnowledgeStore store,
    IKnowledgeRepresentationService knowledge,
    IContextMemoryService memory,
    IModelService models)
{
    public const string IndexVersion = "idx-v1";

    public async Task<ExtractionOutcome> CommitAsync(
        string tenantId, string resourceId, string contentHash, IReadOnlyList<ExtractedNode> extracted, CancellationToken ct = default)
    {
        var resource = await store.GetResourceAsync(tenantId, resourceId, ct);
        if (resource is null) return new ExtractionOutcome(ExtractionStatus.NotFound);

        IReadOnlyList<KnowledgeNode> nodes;
        try
        {
            nodes = knowledge.BuildHierarchy(resource, extracted);
        }
        catch (HierarchyValidationException ex)
        {
            await store.MarkFailedAsync(tenantId, resourceId, $"Invalid extraction: {ex.Message}", ct);
            return new ExtractionOutcome(ExtractionStatus.Invalid, Error: ex.Message);
        }

        var model = await models.GetInfoAsync(ct);
        var passages = nodes.Where(n => n.OriginalContent is not null).ToList();
        var embeddings = new Dictionary<string, float[]>();
        if (passages.Count > 0)
        {
            // Contextual embedding: the heading path goes in front of the passage text, the same way
            // a human would read it with its surrounding context (system_architecture.md "Multi-Dimensional Indexing").
            var vectors = await models.EmbedPassagesAsync(passages.Select(n => $"{n.Title}\n{n.OriginalContent}").ToList(), ct);
            foreach (var (node, vector) in passages.Zip(vectors))
            {
                embeddings[node.NodeId] = vector;
            }
        }

        var commit = await store.CommitVersionAsync(tenantId, resourceId, contentHash, nodes, embeddings, model.EmbeddingModel, ct);
        if (commit is null) return new ExtractionOutcome(ExtractionStatus.NotFound);

        if (commit.Applied)
        {
            if (commit.SupersededVersion is not null)
            {
                await memory.PurgeSupersededAsync(tenantId, resourceId, commit.SupersededVersion, ct);
            }

            var cacheKey = new CacheKey(resourceId, contentHash, IndexVersion, model.EmbeddingModel, tenantId);
            await memory.SetCachedDocumentKnowledgeAsync(cacheKey, nodes, ct);
        }

        return new ExtractionOutcome(ExtractionStatus.Committed, commit.Resource);
    }
}
