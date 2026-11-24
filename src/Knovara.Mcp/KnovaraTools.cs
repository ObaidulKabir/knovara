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

    /// <summary>Must match the API's SourceType enum; a test in Knovara.Api.Tests enforces it.</summary>
    public static readonly IReadOnlyList<string> SourceTypes = ["Document", "Website", "Code"];

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
        var canonical = SearchModes.FirstOrDefault(m => m.Equals(mode, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Unknown mode '{mode}'. Use one of: {string.Join(", ", SearchModes)}.");
        if (topK is < 1 or > 50) throw new McpException("topK must be between 1 and 50.");

        var result = await api.SearchAsync(question, canonical, topK, ct);
        var text = FormatEvidence(result.Evidence, $"mode {canonical}{(result.Reranked ? ", reranked" : "")}");
        text += $"\nEvidence sufficient: {(result.EvidenceSufficient ? "yes" : "no")}";
        if (!string.IsNullOrWhiteSpace(result.FollowUpSearchHint)) text += $"\nSuggested follow-up search: {result.FollowUpSearchHint}";
        return text;
    }

    [McpServerTool(Name = "knovara_list_documents", Title = "List Knovara documents", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List every document registered in the user's Knovara knowledge base with its id, source, type and "
        + "processing status (Pending, Processing, Ready or Failed).")]
    public async Task<string> ListDocuments(CancellationToken ct = default)
    {
        var resources = await api.ListAsync(ct);
        if (resources.Count == 0) return "No documents are registered for this tenant.";

        var sb = new StringBuilder($"{resources.Count} document(s):\n");
        foreach (var r in resources) sb.AppendLine($"- {r.Uri} [{r.Type}] status={r.Status} id={r.ResourceId}");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "knovara_get_document_status", Title = "Get document status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the processing status of one document by its resource id (from knovara_ingest or knovara_list_documents). "
        + "A document can only be retrieved once its status is Ready.")]
    public async Task<string> GetDocumentStatus(
        [Description("The resource id.")] string resourceId,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        var r = await api.GetAsync(resourceId, ct)
            ?? throw new McpException($"No document with id '{resourceId}' exists for this tenant.");
        return $"{r.Uri} [{r.Type}] status={r.Status} version={r.SourceVersion ?? "(not processed yet)"} id={r.ResourceId}";
    }

    [McpServerTool(Name = "knovara_ingest", Title = "Ingest a document", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Queue a document or web page for ingestion into the user's knowledge base. Processing is asynchronous: "
        + "this returns immediately with a resource id; poll knovara_get_document_status until it is Ready. "
        + "The uri must be an http(s) URL or a path inside the ingestion worker's source volume; local Windows/macOS paths are not readable by it.")]
    public async Task<string> Ingest(
        [Description("http(s) URL, or a relative path in the worker's source volume.")] string uri,
        [Description("Document (default), Website or Code.")] string type = "Document",
        CancellationToken ct = default)
    {
        Require(uri, nameof(uri));
        var canonical = SourceTypes.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Unknown type '{type}'. Use one of: {string.Join(", ", SourceTypes)}.");

        var r = await api.IngestAsync(uri, canonical, ct);
        return $"Queued {r.Uri} [{r.Type}] status={r.Status} id={r.ResourceId}. Check progress with knovara_get_document_status.";
    }

    [McpServerTool(Name = "knovara_delete_document", Title = "Delete a document", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("PERMANENTLY delete a document and everything derived from it (passages, embeddings). This cannot be undone. "
        + "Only call it when the user has explicitly asked to delete that specific document.")]
    public async Task<string> DeleteDocument(
        [Description("The resource id to delete.")] string resourceId,
        CancellationToken ct = default)
    {
        Require(resourceId, nameof(resourceId));
        return await api.DeleteAsync(resourceId, ct)
            ? $"Deleted document {resourceId}."
            : $"No document with id '{resourceId}' exists for this tenant; nothing was deleted.";
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

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new McpException($"{name} is required.");
    }
}
