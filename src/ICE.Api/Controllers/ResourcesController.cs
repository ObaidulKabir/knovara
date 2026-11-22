using ICE.Core.Contracts;
using ICE.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ICE.Api.Controllers;

public record IngestRequest(string Uri, SourceType Type, string TenantId);

[ApiController]
[Route("api/resources")]
public sealed class ResourcesController : ControllerBase
{
    private readonly IResourceIngestionService _ingestion;
    private readonly IKnowledgeRepresentationService _knowledge;
    private readonly IIndexManagementService _index;
    private readonly IContextMemoryService _memory;

    public ResourcesController(
        IResourceIngestionService ingestion,
        IKnowledgeRepresentationService knowledge,
        IIndexManagementService index,
        IContextMemoryService memory)
    {
        _ingestion = ingestion;
        _knowledge = knowledge;
        _index = index;
        _memory = memory;
    }

    /// <summary>Registers a source and runs it through ingestion -> hierarchy -> indexing (Phase 1/2 pipeline).</summary>
    [HttpPost("ingest")]
    public async Task<ActionResult<ResourceReference>> Ingest([FromBody] IngestRequest request, CancellationToken ct)
    {
        var resource = await _ingestion.RegisterResourceAsync(request.Uri, request.Type, request.TenantId, ct);
        var rawNodes = await _ingestion.IngestAsync(resource, ct);
        var nodes = await _knowledge.BuildHierarchyAsync(resource, rawNodes, ct);

        foreach (var node in nodes)
        {
            await _index.IndexNodeAsync(node, IndexingLevel.Basic, ct);
        }

        var cacheKey = new CacheKey(resource.ResourceId, resource.SourceVersion, "idx-v1", "model-v1", resource.TenantId);
        await _memory.SetCachedDocumentKnowledgeAsync(cacheKey, nodes, ct);

        return Ok(resource);
    }

    /// <summary>
    /// Accepts raw structural nodes extracted out-of-process (e.g. by the Python
    /// Docling worker in workers/document-processing) and runs the rest of the
    /// pipeline: hierarchy -> indexing -> document cache.
    /// </summary>
    [HttpPost("{resourceId}/nodes")]
    public async Task<ActionResult<IReadOnlyList<KnowledgeNode>>> SubmitExtractedNodes(
        string resourceId,
        [FromBody] IReadOnlyList<KnowledgeNode> rawNodes,
        [FromQuery] string tenantId,
        [FromQuery] string sourceVersion,
        CancellationToken ct)
    {
        var resource = new ResourceReference(resourceId, SourceType.Document, Uri: resourceId, sourceVersion, tenantId);
        var nodes = await _knowledge.BuildHierarchyAsync(resource, rawNodes, ct);

        foreach (var node in nodes)
        {
            await _index.IndexNodeAsync(node, IndexingLevel.Basic, ct);
        }

        var cacheKey = new CacheKey(resource.ResourceId, resource.SourceVersion, "idx-v1", "model-v1", tenantId);
        await _memory.SetCachedDocumentKnowledgeAsync(cacheKey, nodes, ct);

        return Ok(nodes);
    }
}
