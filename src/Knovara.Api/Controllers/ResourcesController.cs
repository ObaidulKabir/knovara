using System.Text.RegularExpressions;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Knovara.Api.Controllers;

public record IngestRequest(string Uri, SourceType Type);
public record VersionCheckRequest(string ContentHash);
public record VersionCheckResponse(bool Changed);
public record SubmitExtractionRequest(string ContentHash, IReadOnlyList<ExtractedNode> Nodes);
public record ExtractionFailureRequest(string Error);

/// <summary>
/// Ingestion is asynchronous: <c>ingest</c> registers the resource and queues it; the Docling worker
/// then calls <c>version</c>, and <c>nodes</c> or <c>failure</c>, on the same resource.
/// </summary>
[ApiController]
[Route("api/resources")]
public sealed partial class ResourcesController(
    IKnowledgeStore store,
    IIngestionQueue queue,
    IKnowledgeRepresentationService knowledge,
    IContextMemoryService memory,
    IModelService models) : ControllerBase
{
    private const string IndexVersion = "idx-v1";
    private const int MaxUriLength = 2_048;
    private const int MaxErrorLength = 2_000;

    [HttpPost("ingest")]
    public async Task<ActionResult<ResourceReference>> Ingest(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] IngestRequest request,
        CancellationToken ct)
    {
        if (request.Type == SourceType.Code)
        {
            return Problem(statusCode: 400, title: "Code ingestion arrives in Phase 4.",
                detail: "Phase 1 accepts Document and Website sources.");
        }

        if (!IsAcceptableUri(request.Uri))
        {
            return Problem(statusCode: 400, title: "Unsupported URI.",
                detail: "Use an http(s) URL, or a relative path inside the worker's source folder (no '..', no absolute paths).");
        }

        var resource = await store.RegisterResourceAsync(tenantId, request.Uri, request.Type, ct);
        await queue.EnqueueAsync(new IngestionJob(resource.ResourceId, tenantId, resource.Uri, resource.Type), ct);

        return AcceptedAtAction(nameof(Get), new { resourceId = resource.ResourceId }, resource);
    }

    [HttpGet("{resourceId}")]
    public async Task<ActionResult<ResourceReference>> Get(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        CancellationToken ct)
    {
        var resource = await store.GetResourceAsync(tenantId, resourceId, ct);
        return resource is null ? NotFound() : Ok(resource);
    }

    /// <summary>Every resource registered for the caller's tenant - the document-management panel's list view.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ResourceReference>>> List(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        CancellationToken ct)
    {
        return Ok(await store.ListResourcesAsync(tenantId, ct));
    }

    /// <summary>
    /// Deletes a resource and everything derived from it (knowledge, embeddings, relationships - all
    /// cascade from the one row). Does not cancel a job already queued for it; the worker's own calls
    /// against a deleted resource already fail with 404, the same as any other missing resource.
    /// </summary>
    [HttpDelete("{resourceId}")]
    public async Task<IActionResult> Delete(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        CancellationToken ct)
    {
        return await store.DeleteResourceAsync(tenantId, resourceId, ct) ? NoContent() : NotFound();
    }

    /// <summary>Called by the worker after hashing the fetched source, before parsing it.</summary>
    [HttpPost("{resourceId}/version")]
    public async Task<ActionResult<VersionCheckResponse>> CheckVersion(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        [FromBody] VersionCheckRequest request,
        CancellationToken ct)
    {
        if (!IsContentHash(request.ContentHash)) return Problem(statusCode: 400, title: "contentHash must be 'sha256:' followed by 64 lowercase hex digits.");

        var model = await models.GetInfoAsync(ct);
        return await store.CheckVersionAsync(tenantId, resourceId, request.ContentHash, model.EmbeddingModel, ct) switch
        {
            VersionCheck.NotFound => NotFound(),
            VersionCheck.Unchanged => Ok(new VersionCheckResponse(Changed: false)),
            _ => Ok(new VersionCheckResponse(Changed: true))
        };
    }

    /// <summary>Called by the worker with the extracted hierarchy of a new version.</summary>
    [HttpPost("{resourceId}/nodes")]
    public async Task<ActionResult<ResourceReference>> SubmitExtraction(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        [FromBody] SubmitExtractionRequest request,
        CancellationToken ct)
    {
        if (!IsContentHash(request.ContentHash)) return Problem(statusCode: 400, title: "contentHash must be 'sha256:' followed by 64 lowercase hex digits.");

        var resource = await store.GetResourceAsync(tenantId, resourceId, ct);
        if (resource is null) return NotFound();

        IReadOnlyList<KnowledgeNode> nodes;
        try
        {
            nodes = knowledge.BuildHierarchy(resource, request.Nodes);
        }
        catch (HierarchyValidationException ex)
        {
            await store.MarkFailedAsync(tenantId, resourceId, $"Invalid extraction: {ex.Message}", ct);
            return Problem(statusCode: 400, title: "Invalid extraction.", detail: ex.Message);
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

        var commit = await store.CommitVersionAsync(tenantId, resourceId, request.ContentHash, nodes, embeddings, model.EmbeddingModel, ct);
        if (commit is null) return NotFound();

        if (commit.Applied)
        {
            if (commit.SupersededVersion is not null)
            {
                await memory.PurgeSupersededAsync(tenantId, resourceId, commit.SupersededVersion, ct);
            }

            var cacheKey = new CacheKey(resourceId, request.ContentHash, IndexVersion, model.EmbeddingModel, tenantId);
            await memory.SetCachedDocumentKnowledgeAsync(cacheKey, nodes, ct);
        }

        return Ok(commit.Resource);
    }

    [HttpPost("{resourceId}/failure")]
    public async Task<IActionResult> ReportFailure(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        [FromBody] ExtractionFailureRequest request,
        CancellationToken ct)
    {
        var error = request.Error.Length > MaxErrorLength ? request.Error[..MaxErrorLength] : request.Error;
        return await store.MarkFailedAsync(tenantId, resourceId, error, ct) ? NoContent() : NotFound();
    }

    private static bool IsAcceptableUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxUriLength) return false;

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return true;
        }

        var segments = uri.Replace('\\', '/').Split('/');
        return !uri.StartsWith('/') && !uri.StartsWith('\\') && !uri.Contains(':')
               && segments.All(s => s.Length > 0 && s != "." && s != "..");
    }

    private static bool IsContentHash(string? value) => value is not null && ContentHashPattern().IsMatch(value);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ContentHashPattern();
}
