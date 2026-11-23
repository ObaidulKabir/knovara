using ICE.Core.Contracts;
using ICE.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ICE.Api.Controllers;

public record IngestRequest(string Uri, SourceType Type, string? ContentHash);

[ApiController]
[Route("api/resources")]
public sealed class ResourcesController : ControllerBase
{
    private const string IndexVersion = "idx-v1";
    private const string ModelVersion = "model-v1";
    private const string InitialVersion = "v1";

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

    /// <summary>
    /// Registers a source and, only if this is a new version, runs ingestion -> hierarchy -> indexing.
    /// An unchanged source stops at the version check.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<ActionResult<ResourceReference>> Ingest(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] IngestRequest request,
        CancellationToken ct)
    {
        var registration = await _ingestion.RegisterResourceAsync(
            request.Uri, request.Type, tenantId, request.ContentHash ?? InitialVersion, ct);

        if (!registration.IsNewVersion)
        {
            return Ok(registration.Resource);
        }

        var resource = registration.Resource;
        await SupersedeAsync(registration, ct);

        var rawNodes = await _ingestion.IngestAsync(resource, ct);
        await BuildAndIndexAsync(resource, rawNodes, ct);

        return Ok(resource);
    }

    /// <summary>
    /// Accepts raw structural nodes extracted out-of-process (e.g. by the Python Docling worker in
    /// workers/document-processing) for an already-registered resource, and runs hierarchy -> indexing -> document cache.
    /// </summary>
    [HttpPost("{resourceId}/nodes")]
    public async Task<ActionResult<IReadOnlyList<KnowledgeNode>>> SubmitExtractedNodes(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        [FromBody] IReadOnlyList<KnowledgeNode> rawNodes,
        CancellationToken ct)
    {
        var resource = await _ingestion.GetResourceAsync(resourceId, ct);
        if (resource is null || resource.TenantId != tenantId)
        {
            return NotFound();
        }

        if (rawNodes.Any(n => n.ResourceId != resource.ResourceId))
        {
            return BadRequest("Every node must belong to the resource in the route.");
        }

        var nodes = await BuildAndIndexAsync(resource, rawNodes, ct);
        return Ok(nodes);
    }

    private async Task SupersedeAsync(ResourceRegistration registration, CancellationToken ct)
    {
        if (registration.SupersededVersion is null) return;

        var resource = registration.Resource;
        await _index.InvalidateAsync(resource, ct);
        await _memory.PurgeSupersededAsync(resource.TenantId, resource.ResourceId, registration.SupersededVersion, ct);
    }

    private async Task<IReadOnlyList<KnowledgeNode>> BuildAndIndexAsync(ResourceReference resource, IReadOnlyList<KnowledgeNode> rawNodes, CancellationToken ct)
    {
        var nodes = await _knowledge.BuildHierarchyAsync(resource, rawNodes, ct);

        foreach (var node in nodes)
        {
            await _index.IndexNodeAsync(resource, node, IndexingLevel.Basic, ct);
        }

        var edges = await _knowledge.ExtractRelationshipsAsync(resource, nodes, ct);
        await _index.IndexRelationshipsAsync(resource, edges, ct);

        var cacheKey = new CacheKey(resource.ResourceId, resource.SourceVersion, IndexVersion, ModelVersion, resource.TenantId);
        await _memory.SetCachedDocumentKnowledgeAsync(cacheKey, nodes, ct);

        return nodes;
    }
}
