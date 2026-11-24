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
public record UploadRequest(string FileName, string? Content = null, string? ContentBase64 = null);
public record BulkDeleteRequest(IReadOnlyList<string>? ResourceIds = null, ResourceStatus? Status = null);
public record BulkDeleteResponse(IReadOnlyList<string> Deleted, IReadOnlyList<string> NotDeleted);

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
    private const int MaxPageSize = 500;
    private const int MaxPassagePageSize = 200;
    private const int MaxBulkDelete = 1_000;
    private const int MaxUploadBytes = 25 * 1024 * 1024;

    /// <summary>Formats the worker can extract; an upload outside this list would only fail later, so it is refused up front.</summary>
    private static readonly string[] UploadExtensions = [".pdf", ".docx", ".pptx", ".html", ".htm", ".md", ".markdown"];

    /// <summary>A safe plain file name with a supported extension, or null. '.txt' becomes '.md' (markdown reads plain text as-is).</summary>
    internal static string? NormalizeUploadName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var name = fileName.Replace('\\', '/').Split('/').Last().Trim();
        if (name.Length is 0 or > 200 || name.Any(char.IsControl)) return null;

        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name = name[..^4] + ".md";
        return UploadExtensions.Any(e => name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? name : null;
    }

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

    /// <summary>
    /// The caller's resources, most recently updated first. Filter by <c>status</c>, <c>type</c> and a case-insensitive
    /// <c>q</c> substring of the URI; page with <c>limit</c> (1-500, default 100) and <c>offset</c>. The total number
    /// of matches is in the <c>X-Total-Count</c> header, so the body stays a plain array.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ResourceReference>>> List(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromQuery] ResourceStatus? status,
        [FromQuery] SourceType? type,
        [FromQuery] string? q,
        CancellationToken ct,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        if (limit is < 1 or > MaxPageSize) return Problem(statusCode: 400, title: $"limit must be between 1 and {MaxPageSize}.");
        if (offset < 0) return Problem(statusCode: 400, title: "offset must not be negative.");

        var page = await store.QueryResourcesAsync(tenantId, new ResourceFilter(status, type, q, limit, offset), ct);
        Response.Headers["X-Total-Count"] = page.Total.ToString();
        return Ok(page.Items);
    }

    /// <summary>What the caller has stored: resources by status and type, passage and embedding counts, extracted text size, uploaded bytes.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<ResourceStats>> Stats(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        CancellationToken ct)
        => Ok(await store.GetStatsAsync(tenantId, ct));

    /// <summary>The extracted passages of one resource in reading order (text truncated to 500 characters) - to check what was actually stored.</summary>
    [HttpGet("{resourceId}/passages")]
    public async Task<ActionResult<PassagePage>> Passages(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        CancellationToken ct,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (limit is < 1 or > MaxPassagePageSize) return Problem(statusCode: 400, title: $"limit must be between 1 and {MaxPassagePageSize}.");
        if (offset < 0) return Problem(statusCode: 400, title: "offset must not be negative.");

        var page = await store.ListPassagesAsync(tenantId, resourceId, limit, offset, ct);
        return page is null ? NotFound() : Ok(page);
    }

    /// <summary>
    /// Adds a document from content the caller holds, with no shared volume or public URL needed. Send the file as
    /// <c>contentBase64</c> (any supported format) or, for text/markdown, as plain <c>content</c>. Uploading the same
    /// <c>fileName</c> again replaces it and re-processes it. Supported: pdf, docx, pptx, html, md (a .txt name is stored as .md).
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes * 2)]
    public async Task<ActionResult<ResourceReference>> Upload(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] UploadRequest request,
        CancellationToken ct)
    {
        var fileName = NormalizeUploadName(request.FileName);
        if (fileName is null)
        {
            return Problem(statusCode: 400, title: "Unsupported fileName.",
                detail: $"Use a plain file name (no folders) ending in {string.Join(", ", UploadExtensions)} ('.txt' is accepted and stored as '.md').");
        }

        if ((request.Content is null) == (request.ContentBase64 is null))
        {
            return Problem(statusCode: 400, title: "Send exactly one of content (text) or contentBase64.");
        }

        byte[] bytes;
        try
        {
            bytes = request.Content is not null ? System.Text.Encoding.UTF8.GetBytes(request.Content) : Convert.FromBase64String(request.ContentBase64!);
        }
        catch (FormatException)
        {
            return Problem(statusCode: 400, title: "contentBase64 is not valid base64.");
        }

        if (bytes.Length == 0) return Problem(statusCode: 400, title: "The document is empty.");
        if (bytes.Length > MaxUploadBytes) return Problem(statusCode: 413, title: $"The document is larger than {MaxUploadBytes / (1024 * 1024)} MB.");

        var resource = await store.SaveUploadAsync(tenantId, fileName, bytes, ct);
        await queue.EnqueueAsync(new IngestionJob(resource.ResourceId, tenantId, resource.Uri, resource.Type), ct);

        return AcceptedAtAction(nameof(Get), new { resourceId = resource.ResourceId }, resource);
    }

    /// <summary>The original bytes of an uploaded document. Also what the worker fetches to process it. 404 for URL-sourced resources.</summary>
    [HttpGet("{resourceId}/content")]
    public async Task<IActionResult> Content(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        CancellationToken ct)
    {
        var upload = await store.GetUploadAsync(tenantId, resourceId, ct);
        return upload is null ? NotFound() : File(upload.Content, "application/octet-stream", upload.FileName);
    }

    /// <summary>Re-queues an existing resource for processing, e.g. a Failed one after fixing its source, or to pick up a changed web page.</summary>
    [HttpPost("{resourceId}/reingest")]
    public async Task<ActionResult<ResourceReference>> Reingest(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string resourceId,
        CancellationToken ct)
    {
        var existing = await store.GetResourceAsync(tenantId, resourceId, ct);
        if (existing is null) return NotFound();

        var resource = await store.RegisterResourceAsync(tenantId, existing.Uri, existing.Type, ct);
        await queue.EnqueueAsync(new IngestionJob(resource.ResourceId, tenantId, resource.Uri, resource.Type), ct);
        return AcceptedAtAction(nameof(Get), new { resourceId = resource.ResourceId }, resource);
    }

    /// <summary>
    /// Deletes many resources at once, by explicit <c>resourceIds</c> and/or by <c>status</c> (e.g. all Failed).
    /// When both are given a resource must match both. At least one is required: there is deliberately no "delete everything".
    /// Returns what was deleted and which requested ids were not (missing, another tenant's, or a different status).
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult<BulkDeleteResponse>> BulkDelete(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        [FromBody] BulkDeleteRequest request,
        CancellationToken ct)
    {
        var ids = request.ResourceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if ((ids is null || ids.Count == 0) && request.Status is null)
        {
            return Problem(statusCode: 400, title: "Say what to delete.",
                detail: "Provide resourceIds and/or a status. Deleting everything must be done by listing and naming the ids.");
        }
        if (ids is { Count: > MaxBulkDelete }) return Problem(statusCode: 400, title: $"At most {MaxBulkDelete} resourceIds per request.");
        if (request.ResourceIds is not null && ids!.Count == 0) return Problem(statusCode: 400, title: "resourceIds contains no ids.");

        var deleted = await store.DeleteResourcesAsync(tenantId, ids, request.Status, ct);
        var notDeleted = ids?.Except(deleted).ToList() ?? [];
        return Ok(new BulkDeleteResponse(deleted, notDeleted));
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
