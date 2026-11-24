using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Knovara.Mcp;

/// <summary>The tools Knovara exposes to MCP clients. Each maps onto one existing API endpoint.</summary>
[McpServerToolType]
public sealed class KnovaraTools(KnovaraApiClient api)
{
    /// <summary>Must match the API's RetrievalMode enum; a test in Knovara.Api.Tests enforces it.</summary>
    public static readonly IReadOnlyList<string> SearchModes = ["Adaptive", "Lexical", "Semantic", "Hybrid", "HybridRerank"];

    /// <summary>
    /// The SourceType values the API's ingest endpoint accepts. The enum also has Code, which the API rejects until
    /// code ingestion exists, so it is deliberately not offered; a test in Knovara.Api.Tests pins this to the enum.
    /// </summary>
    public static readonly IReadOnlyList<string> SourceTypes = ["Document", "Website"];

    /// <summary>Must match the API's ResourceStatus enum; a test in Knovara.Api.Tests enforces it.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["Pending", "Processing", "Ready", "Failed"];

    private const int MaxIdsPerDelete = 1000;

    [McpServerTool(Name = "knovara_ask", Title = "Ask Knovara", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieve the most relevant, source-cited passages from the user's Knovara knowledge base for a question, "
        + "packed to fit a token budget. Use this to ground an answer in the user's own documents. Returns passages with "
        + "their source and version; it does not generate an answer itself.")]
    public async Task<string> Ask(
        [Description("The question or topic to find evidence for.")] string question,
        [Description("Maximum tokens of evidence to return (default 2000).")] int tokenBudget = 2000,
        CancellationToken ct = default)
    {
        Require(question, nameof(question));
        if (tokenBudget <= 0) throw new McpException("tokenBudget must be greater than 0.");

        var package = await api.AskAsync("mcp", question, tokenBudget, ct);
        return FormatEvidence(package.Evidence, $"{package.EstimatedTokenCount} tokens");
    }

    [McpServerTool(Name = "knovara_search", Title = "Search Knovara", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Run one retrieval mode directly and return the raw ranked passages, without token-budget packing. "
        + "Prefer knovara_ask unless you need a specific mode. Modes: Adaptive (default; hybrid, reranks only when the "
        + "top results are too close), Lexical (keyword), Semantic (embeddings), Hybrid (both, fused), HybridRerank "
        + "(most accurate, slowest).")]
    public async Task<string> Search(
        [Description("What to search for.")] string question,
        [Description("Retrieval mode: Adaptive, Lexical, Semantic, Hybrid or HybridRerank.")] string mode = "Adaptive",
        [Description("How many passages to return (1-50, default 5).")] int topK = 5,
        CancellationToken ct = default)
    {
        Require(question, nameof(question));
        var canonical = Canonical(SearchModes, mode, "mode");
        if (topK is < 1 or > 50) throw new McpException("topK must be between 1 and 50.");

        var result = await api.SearchAsync(question, canonical, topK, ct);
        var text = FormatEvidence(result.Evidence, $"mode {canonical}{(result.Reranked ? ", reranked" : "")}");
        text += $"\nEvidence sufficient: {(result.EvidenceSufficient ? "yes" : "no")}";
        if (!string.IsNullOrWhiteSpace(result.FollowUpSearchHint)) text += $"\nSuggested follow-up search: {result.FollowUpSearchHint}";
        return text;
    }

    // ---- managing the document volume -----------------------------------------------------------

    [McpServerTool(Name = "knovara_list_documents", Title = "List Knovara documents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List documents in the user's Knovara knowledge base, most recently updated first, with id, source, type and "
        + "processing status (Pending, Processing, Ready or Failed; failures include the reason). Filter by status, type or a "
        + "substring of the source, and page through large volumes with limit/offset.")]
    public async Task<string> ListDocuments(
        [Description("Only this status: Pending, Processing, Ready or Failed.")] string? status = null,
        [Description("Only this type: Document or Website.")] string? type = null,
        [Description("Only documents whose source contains this text (case-insensitive).")] string? search = null,
        [Description("Page size, 1-200 (default 50).")] int limit = 50,
        [Description("How many to skip (default 0).")] int offset = 0,
        CancellationToken ct = default)
    {
        var canonicalStatus = status is null ? null : Canonical(Statuses, status, "status");
        var canonicalType = type is null ? null : Canonical(SourceTypes, type, "type");
        if (limit is < 1 or > 200) throw new McpException("limit must be between 1 and 200.");
        if (offset < 0) throw new McpException("offset must not be negative.");

        var page = await api.ListAsync(canonicalStatus, canonicalType, search, limit, offset, ct);
        if (page.Items.Count == 0)
        {
            return page.Total == 0 ? "No documents match." : $"No documents at offset {offset}; {page.Total} match in total.";
        }

        var sb = new StringBuilder($"Showing {offset + 1}-{offset + page.Items.Count} of {page.Total} document(s):\n");
        foreach (var r in page.Items) sb.AppendLine(Describe(r));
        if (offset + page.Items.Count < page.Total) sb.AppendLine($"More available: call again with offset={offset + page.Items.Count}.");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "knovara_get_stats", Title = "Get knowledge base stats", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How much the user has stored: documents by status and type, number of searchable passages and embeddings, "
        + "extracted text size and uploaded bytes. Use it to judge the size of the volume before pruning.")]
    public async Task<string> GetStats(CancellationToken ct = default)
    {
        var s = await api.StatsAsync(ct);
        return new StringBuilder()
            .AppendLine($"{s.Resources} document(s): {Join(s.ByStatus)}")
            .AppendLine($"By type: {Join(s.ByType)}")
            .AppendLine($"{s.Passages:N0} searchable passage(s), {s.EmbeddedPassages:N0} embedded")
            .AppendLine($"{s.ContentCharacters:N0} characters of extracted text; {s.UploadedBytes:N0} bytes of uploaded originals")
            .ToString().TrimEnd();
    }

    [McpServerTool(Name = "knovara_get_document_status", Title = "Get document status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the processing status of one document by its resource id (from knovara_ingest, knovara_upload_document or "
        + "knovara_list_documents). A document can only be retrieved once its status is Ready; if Failed, the reason is included.")]
    public async Task<string> GetDocumentStatus(
        [Description("The resource id.")] string resourceId,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        var r = await api.GetAsync(resourceId, ct)
            ?? throw new McpException($"No document with id '{resourceId}' exists for this tenant.");
        return Describe(r, withVersion: true);
    }

    [McpServerTool(Name = "knovara_list_passages", Title = "Inspect a document's passages", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Show the passages actually extracted and stored from one document (text truncated to 500 characters), "
        + "in reading order - to check that a document was understood as expected.")]
    public async Task<string> ListPassages(
        [Description("The resource id.")] string resourceId,
        [Description("Page size, 1-100 (default 20).")] int limit = 20,
        [Description("How many to skip (default 0).")] int offset = 0,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        if (limit is < 1 or > 100) throw new McpException("limit must be between 1 and 100.");
        if (offset < 0) throw new McpException("offset must not be negative.");

        var page = await api.PassagesAsync(resourceId, limit, offset, ct)
            ?? throw new McpException($"No document with id '{resourceId}' exists for this tenant.");
        if (page.Items.Count == 0) return $"No passages stored for this document ({page.Total} in total); it may not be processed yet.";

        var sb = new StringBuilder($"Passages {offset + 1}-{offset + page.Items.Count} of {page.Total}:\n");
        foreach (var p in page.Items) sb.AppendLine().AppendLine($"[{p.Level}] {p.Title}").AppendLine(p.Content);
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "knovara_upload_document", Title = "Upload a document", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Add a text or markdown document to the user's knowledge base from content you provide - no URL or shared "
        + "folder needed. Processing is asynchronous: poll knovara_get_document_status until Ready. Uploading the same "
        + "fileName again REPLACES that document's content and re-processes it. The fileName must end in .md or .txt "
        + "(.txt is stored as .md); use the REST upload endpoint for pdf/docx/pptx/html.")]
    public async Task<string> UploadDocument(
        [Description("File name such as 'runbook.md'. No folders.")] string fileName,
        [Description("The document's text, preferably markdown with headings (headings become the passage context).")] string content,
        CancellationToken ct = default)
    {
        Require(fileName, nameof(fileName));
        Require(content, nameof(content));
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("fileName must end in .md or .txt for text uploads.");
        }

        var r = await api.UploadAsync(fileName, content, ct);
        return $"Uploaded {r.Uri} status={r.Status} id={r.ResourceId}. Check progress with knovara_get_document_status.";
    }

    [McpServerTool(Name = "knovara_ingest", Title = "Ingest a document by URL", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Queue a web page or document URL for ingestion into the user's knowledge base. Processing is asynchronous: "
        + "this returns immediately with a resource id; poll knovara_get_document_status until it is Ready. "
        + "The uri must be an http(s) URL or a path inside the ingestion worker's source volume; local Windows/macOS paths are not readable by it "
        + "(use knovara_upload_document to send content instead).")]
    public async Task<string> Ingest(
        [Description("http(s) URL, or a relative path in the worker's source volume.")] string uri,
        [Description("Document (default) or Website.")] string type = "Document",
        CancellationToken ct = default)
    {
        Require(uri, nameof(uri));
        var canonical = Canonical(SourceTypes, type, "type");

        var r = await api.IngestAsync(uri, canonical, ct);
        return $"Queued {r.Uri} [{r.Type}] status={r.Status} id={r.ResourceId}. Check progress with knovara_get_document_status.";
    }

    [McpServerTool(Name = "knovara_reingest_document", Title = "Re-process a document", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Queue an existing document for processing again - to retry a Failed one, or to pick up changes to a web page. "
        + "Its current content stays searchable until the new version is ready.")]
    public async Task<string> ReingestDocument(
        [Description("The resource id.")] string resourceId,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        var r = await api.ReingestAsync(resourceId, ct)
            ?? throw new McpException($"No document with id '{resourceId}' exists for this tenant.");
        return $"Queued again: {Describe(r)}";
    }

    [McpServerTool(Name = "knovara_delete_document", Title = "Delete a document", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("PERMANENTLY delete one document and everything derived from it (passages, embeddings, uploaded original). "
        + "This cannot be undone. Only call it when the user has explicitly asked to delete that specific document.")]
    public async Task<string> DeleteDocument(
        [Description("The resource id to delete.")] string resourceId,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        return await api.DeleteAsync(resourceId, ct)
            ? $"Deleted document {resourceId}."
            : $"No document with id '{resourceId}' exists for this tenant; nothing was deleted.";
    }

    [McpServerTool(Name = "knovara_delete_documents", Title = "Delete several documents", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("PERMANENTLY delete many documents at once, by explicit resourceIds and/or by status (for example every Failed "
        + "document). When both are given a document must match both. At least one is required - there is no 'delete everything'. "
        + "This cannot be undone: list what will match first, and only call it when the user has explicitly asked for that deletion.")]
    public async Task<string> DeleteDocuments(
        [Description("Resource ids to delete (max 1000).")] string[]? resourceIds = null,
        [Description("Only delete documents in this status: Pending, Processing, Ready or Failed.")] string? status = null,
        CancellationToken ct = default)
    {
        var ids = resourceIds?.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        var canonicalStatus = status is null ? null : Canonical(Statuses, status, "status");
        if ((ids is null || ids.Count == 0) && canonicalStatus is null)
        {
            throw new McpException("Provide resourceIds and/or a status. Deleting everything must be done by naming the ids.");
        }
        if (ids is { Count: > MaxIdsPerDelete }) throw new McpException($"At most {MaxIdsPerDelete} resourceIds per call.");

        var result = await api.BulkDeleteAsync(ids, canonicalStatus, ct);
        var text = $"Deleted {result.Deleted.Count} document(s).";
        if (result.NotDeleted.Count > 0)
        {
            text += $" Not deleted (missing, or not in the requested status): {string.Join(", ", result.NotDeleted)}.";
        }
        return text;
    }

    internal static string FormatEvidence(IReadOnlyList<EvidenceItem> evidence, string summary)
    {
        if (evidence.Count == 0) return $"No relevant evidence found ({summary}).";

        var sb = new StringBuilder($"{evidence.Count} passage(s) ({summary}):\n");
        for (var i = 0; i < evidence.Count; i++)
        {
            var e = evidence[i];
            sb.AppendLine().AppendLine($"[{i + 1}] {e.Title} - source {e.ResourceId} (version {e.SourceVersion}, relevance {e.RelevanceScore:0.###})");
            sb.AppendLine(e.Content);
        }
        return sb.ToString().TrimEnd();
    }

    private static string Describe(ResourceReference r, bool withVersion = false)
    {
        var text = $"- {r.Uri} [{r.Type}] status={r.Status} id={r.ResourceId}";
        if (withVersion) text += $" version={r.SourceVersion ?? "(not processed yet)"}";
        if (!string.IsNullOrWhiteSpace(r.Error)) text += $"\n    reason: {r.Error}";
        return text;
    }

    private static string Join(Dictionary<string, int> counts) => string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"));

    private static string Canonical(IReadOnlyList<string> allowed, string value, string name) =>
        allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Unknown {name} '{value}'. Use one of: {string.Join(", ", allowed)}.");

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new McpException($"{name} is required.");
    }
}
