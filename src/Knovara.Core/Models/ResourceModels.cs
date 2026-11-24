namespace Knovara.Core.Models;

public enum SourceType
{
    Code,
    Document,
    Website
}

public enum ResourceStatus
{
    Pending,
    Processing,
    Ready,
    Failed
}

/// <summary>
/// A registered source. <see cref="SourceVersion"/> is the content hash of the last successfully
/// extracted version, or null until the first extraction completes.
/// </summary>
public record ResourceReference(
    string ResourceId,
    SourceType Type,
    string Uri,
    string? SourceVersion,
    string TenantId,
    ResourceStatus Status,
    string? Error = null);

/// <summary>Narrows a resource listing. <c>UriContains</c> is a case-insensitive substring match.</summary>
public record ResourceFilter(
    ResourceStatus? Status = null,
    SourceType? Type = null,
    string? UriContains = null,
    int Limit = 100,
    int Offset = 0);

/// <summary>One page of resources plus how many matched the filter in total.</summary>
public record ResourcePage(IReadOnlyList<ResourceReference> Items, int Total);

/// <summary>How much a tenant has stored: the numbers needed to decide whether to prune.</summary>
public record ResourceStats(
    int Resources,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByType,
    int Passages,
    int EmbeddedPassages,
    long ContentCharacters,
    long UploadedBytes);

/// <summary>A stored passage as shown when inspecting what was extracted from a resource.</summary>
public record PassageSummary(string NodeId, string Level, string Title, string? Content);

public record PassagePage(IReadOnlyList<PassageSummary> Items, int Total);

/// <summary>The original bytes of a document uploaded through the API (as opposed to fetched from a URL).</summary>
public record StoredUpload(string FileName, byte[] Content);

/// <summary>A node in the hierarchical knowledge representation (document/section/passage).</summary>
public record KnowledgeNode(
    string NodeId,
    string ResourceId,
    string? ParentNodeId,
    string Level,
    string Title,
    string? Summary,
    string? OriginalContent,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// A node as produced by an extractor (e.g. the Docling worker), identified by ids local to one
/// extraction. Parents must appear before their children; the first node is the document root.
/// </summary>
public record ExtractedNode(
    string LocalId,
    string? ParentLocalId,
    string Level,
    string Title,
    string? Content,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>An extracted entity or relationship edge (e.g., class -> method, service -> dependency).</summary>
public record RelationshipEdge(
    string FromNodeId,
    string ToNodeId,
    string RelationType);

/// <summary>A passage as it exists in the knowledge store right now, used to verify evidence.</summary>
public record CurrentPassage(
    string NodeId,
    string ResourceId,
    string SourceVersion,
    string Content);

/// <summary>Uploaded documents are registered under <c>upload://{fileName}</c>; the worker recognises the prefix and fetches the bytes from the API.</summary>
public static class UploadUri
{
    public const string Prefix = "upload://";

    public static string For(string fileName) => Prefix + fileName;

    public static bool IsUpload(string uri) => uri.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>A request for the extraction worker to fetch and process a resource.</summary>
public record IngestionJob(
    string ResourceId,
    string TenantId,
    string Uri,
    SourceType SourceType);
